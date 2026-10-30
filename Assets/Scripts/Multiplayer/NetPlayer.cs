using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ProtocolExecution
{
    /// <summary>
    /// Jugador de red.
    ///
    /// [RED] Movimiento con autoridad del dueno (prediccion local, GDD 4.5): cada
    /// jugador se mueve en su propia maquina y publica su posicion en
    /// <see cref="NetPosition"/>. El host no re-simula cada input, asi no hay
    /// rollbacks que se sientan como lag; solo reconcilia cuando la posicion es
    /// imposible (<see cref="ServerValidatePosition"/>). Los demas peers
    /// interpolan hacia la ultima posicion recibida.
    ///
    /// Lo que si decide el host: disparos (<see cref="RequestFireServerRpc"/>),
    /// dano, vida, caidas y teletransportes. El juego es cooperativo, asi que
    /// confiar en la posicion del cliente no da ventaja competitiva (§4.4).
    /// </summary>
    public class NetPlayer : NetworkBehaviour
    {
        /// <summary>Jugadores vivos registrados. Lo usan enemigos, jefe y proyectiles.</summary>
        public static readonly List<NetPlayer> All = new List<NetPlayer>();

        [Header("Referencias")]
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField] private Transform muzzle;
        [SerializeField] private Renderer bodyRenderer;

        [Header("Movimiento")]
        [SerializeField] private float radius = 0.45f;
        [SerializeField] private float dashDistance = 4.5f;
        [SerializeField] private LayerMask wallMask = ~0;

        [Header("Reconciliacion suave (GDD 4.5)")]
        [Tooltip("Metros extra que el host tolera antes de corregir una posicion (latencia, redondeos).")]
        [SerializeField] private float reconcileSlack = 1.5f;

        [Header("Combate")]
        [SerializeField] private float downedDuration = 10f;
        [SerializeField] private float reviveHealthFraction = 0.5f;

        // ---------------- Estado replicado ----------------

        /// <summary>Slot 0..2, asignado por el host al aparecer.</summary>
        public readonly NetworkVariable<int> SpawnSlot = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<FixedString64Bytes> PlayerName = new NetworkVariable<FixedString64Bytes>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<int> CharacterIndex = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<bool> IsReady = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Bonificaciones meta del jugador. Las envia su propio dueno porque el
        // perfil es local a cada cliente; el host las usa para resolver dano y
        // vida de forma autoritativa. §4.4 acepta esta postura relajada ante el
        // cheating porque el juego es cooperativo y no hay ventaja competitiva.
        public readonly NetworkVariable<float> BonusHealth = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<float> BonusDamagePercent = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<float> BonusSpeedPercent = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<float> DamageReduction = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<float> Health = new NetworkVariable<float>(
            1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> MaxHealth = new NetworkVariable<float>(
            1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> IsDowned = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> Kills = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> Deaths = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Hora del servidor hasta la que dura el arma sobrecargada (objeto para recoger).</summary>
        public readonly NetworkVariable<double> OverchargeUntil = new NetworkVariable<double>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>[RED] Posicion que publica el dueno cada frame; NGO la envia a la tasa de tick (30 Hz).</summary>
        public readonly NetworkVariable<Vector3> NetPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<float> NetYaw = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // ---------------- Estado local ----------------

        private float _clientFireCooldown;
        private float _serverFireCooldown;
        private float _dashCooldownRemaining;
        private float _downedTimer;
        private Vector3 _interpPosition;
        private float _interpYaw;
        private float _teleportHoldUntil;

        // Reconciliacion suave (solo en el host, sobre jugadores ajenos).
        private Vector3 _serverAccepted;
        private float _serverAcceptedTime;
        private float _serverIgnoreUntil;

        public bool IsAlive => !IsDowned.Value;
        public string DisplayName => PlayerName.Value.ToString();
        public CharacterDef Character => GameData.GetCharacter(CharacterIndex.Value);

        private float Speed => Character.moveSpeed * (1f + BonusSpeedPercent.Value);
        private float Damage => Character.damage * (1f + BonusDamagePercent.Value) * (OverchargeRemaining > 0f ? 1.5f : 1f);

        private void Awake()
        {
            if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<Renderer>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);

            if (IsServer)
            {
                SpawnSlot.Value = All.Count - 1;
            }

            _interpPosition = transform.position;
            _interpYaw = transform.eulerAngles.y;

            if (IsOwner)
            {
                // Nombre visible derivado del id de conexion: evita un campo de
                // texto de entrada y es estable durante toda la sesion.
                PlayerName.Value = $"JUGADOR-{OwnerClientId + 1}";

                // El perfil de mejoras es local: se publica para que el host
                // pueda resolver vida y dano de forma autoritativa.
                BonusHealth.Value = MetaProgression.BonusHealth;
                BonusDamagePercent.Value = MetaProgression.BonusDamagePercent;
                BonusSpeedPercent.Value = MetaProgression.BonusSpeedPercent;
                DamageReduction.Value = MetaProgression.DamageReduction;

                PublishPosition(transform.eulerAngles.y);
            }

            if (IsServer && !IsOwner)
            {
                _serverAccepted = transform.position;
                _serverAcceptedTime = Time.time;
                NetPosition.OnValueChanged += ServerValidatePosition;
            }
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            NetPosition.OnValueChanged -= ServerValidatePosition;
        }

        // ---------------- Ciclo principal ----------------

        private void Update()
        {
            RunController run = RunController.Instance;
            bool runActive = run != null && run.CurrentPhase == RunPhase.Playing;

            if (runActive && !IsDowned.Value && IsOwner) UpdateOwnerInput();

            // Host incluido: todo jugador ajeno se dibuja interpolando lo que publico su dueno.
            if (!IsOwner) UpdateRemoteInterpolation();

            if (IsServer && IsDowned.Value) UpdateServerDowned();

            UpdateBodyColor();
        }

        // ---------------- Dueno del objeto: movimiento local ----------------

        private void UpdateOwnerInput()
        {
            ReadInput(out Vector3 move, out float yaw, out bool dashRequested, out bool fireRequested);

            ApplyMovement(move, dashRequested, Time.deltaTime, out Vector3 newPosition);
            transform.position = newPosition;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            // [RED] Se manda la posicion tal cual; el host solo la corrige si es imposible.
            PublishPosition(yaw);

            if (_dashCooldownRemaining > 0f) _dashCooldownRemaining -= Time.deltaTime;
            if (_clientFireCooldown > 0f) _clientFireCooldown -= Time.deltaTime;

            // §4.5: el disparo se resuelve en el host, pero el feedback visual y
            // sonoro se genera localmente para que se sienta inmediato.
            if (fireRequested && _clientFireCooldown <= 0f)
            {
                _clientFireCooldown = 1f / Mathf.Max(0.01f, Character.fireRate);
                PlayLocalMuzzleFlash();
                RequestFireServerRpc(GetMuzzleOrigin(), GetAimDirection(yaw));
            }
        }

        private void ApplyMovement(Vector3 move, bool dash, float dt, out Vector3 newPosition)
        {
            Vector3 direction = move.sqrMagnitude > 0.0001f ? move.normalized : Vector3.zero;
            bool dashAllowed = direction != Vector3.zero && _dashCooldownRemaining <= 0f;

            Vector3 displacement;
            if (dash && dashAllowed)
            {
                displacement = direction * dashDistance;
                _dashCooldownRemaining = Character.dashCooldown;
            }
            else
            {
                displacement = direction * Speed * dt;
            }

            newPosition = transform.position + ClampAgainstWalls(transform.position, displacement);
        }

        /// <summary>
        /// Resuelve el choque contra paredes y puertas con un SphereCast. Se
        /// ignoran los triggers (los volumenes de deteccion de sala no deben
        /// frenar al jugador) y la capa de combatientes (enemigos y proyectiles
        /// no bloquean el paso).
        /// </summary>
        private Vector3 ClampAgainstWalls(Vector3 position, Vector3 displacement)
        {
            if (displacement.sqrMagnitude < 0.0000001f) return Vector3.zero;

            Vector3 origin = position + Vector3.up * 0.9f;
            float distance = displacement.magnitude;
            Vector3 dir = displacement / distance;

            if (Physics.SphereCast(origin, radius, dir, out RaycastHit hit, distance,
                    wallMask, QueryTriggerInteraction.Ignore))
            {
                float allowed = Mathf.Max(0f, hit.distance - 0.02f);
                return dir * allowed;
            }

            return displacement;
        }

        private void PublishPosition(float yaw)
        {
            NetPosition.Value = transform.position;
            NetYaw.Value = yaw;
        }

        // ---------------- Host: reconciliacion suave (GDD 4.5) ----------------

        /// <summary>
        /// [RED] Reconciliacion suave: el host revisa cada posicion que publica un
        /// cliente y la corrige SOLO si es imposible (mas lejos de lo que se puede
        /// mover en ese tiempo, o atravesando una pared o una puerta cerrada). En
        /// juego normal nunca corrige, asi que no agrega lag.
        /// </summary>
        private void ServerValidatePosition(Vector3 previous, Vector3 next)
        {
            if (Time.time < _serverIgnoreUntil) return;

            // Tope de 0.5 s: quedarse quieto no "acumula" distancia permitida.
            float elapsed = Mathf.Clamp(Time.time - _serverAcceptedTime, 1f / 30f, 0.5f);
            float allowed = Speed * elapsed * 1.25f + dashDistance + reconcileSlack;

            Vector3 delta = next - _serverAccepted;
            delta.y = 0f;

            if (delta.magnitude <= allowed && !PathBlocked(_serverAccepted, next))
            {
                _serverAccepted = next;
                _serverAcceptedTime = Time.time;
                return;
            }

            // Los paquetes viejos que sigan en camino no vuelven a disparar la correccion.
            _serverIgnoreUntil = Time.time + 0.3f;
            CorrectPositionRpc(_serverAccepted);
        }

        /// <summary>True si entre dos posiciones hay una pared o puerta cerrada en el host.</summary>
        private bool PathBlocked(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            d.y = 0f;
            float distance = d.magnitude;
            if (distance < 0.05f) return false;

            // Radio algo menor y 0.3 m de tolerancia: rozar una pared no cuenta como atravesarla.
            return Physics.SphereCast(from + Vector3.up * 0.9f, radius * 0.8f, d / distance, out RaycastHit hit,
                       distance, wallMask, QueryTriggerInteraction.Ignore)
                   && hit.distance < distance - 0.3f;
        }

        /// <summary>[RED] El host devuelve al dueno a la ultima posicion valida.</summary>
        [Rpc(SendTo.Owner)]
        private void CorrectPositionRpc(Vector3 position)
        {
            transform.position = position;
            PublishPosition(transform.eulerAngles.y);
        }

        // ---------------- Jugadores ajenos: interpolacion ----------------

        private void UpdateRemoteInterpolation()
        {
            // Tras un teletransporte la NetPosition vieja sigue llegando hasta que
            // el dueno publica la nueva: se ignora un momento para no volver atras.
            if (Time.time < _teleportHoldUntil) return;

            // [RED] Interpolacion: suaviza los saltos entre paquetes de 30 Hz.
            const float smoothing = 16f;
            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

            _interpPosition = Vector3.Lerp(_interpPosition, NetPosition.Value, t);
            _interpYaw = Mathf.LerpAngle(_interpYaw, NetYaw.Value, t);

            transform.position = _interpPosition;
            transform.rotation = Quaternion.Euler(0f, _interpYaw, 0f);
        }

        // ---------------- Combate ----------------

        [ServerRpc]
        private void RequestFireServerRpc(Vector3 origin, Vector3 direction, ServerRpcParams rpcParams = default)
        {
            // [RED] El disparo si lo valida el host (caido, cadencia) y es quien crea el proyectil.
            if (IsDowned.Value) return;

            float interval = 1f / Mathf.Max(0.01f, Character.fireRate);
            if (_serverFireCooldown > 0f) return;
            _serverFireCooldown = interval;

            if (projectilePrefab == null) return;

            GameObject instance = Instantiate(projectilePrefab, origin, Quaternion.LookRotation(direction));
            var projectile = instance.GetComponent<NetProjectile>();

            if (projectile == null)
            {
                Destroy(instance);
                return;
            }

            projectile.ServerLaunch(
                direction,
                Damage,
                Character.projectileSpeed,
                Character.projectileRange,
                NetProjectile.Team.Players,
                OwnerClientId);
        }

        private void UpdateDownedCooldown()
        {
            if (_serverFireCooldown > 0f) _serverFireCooldown -= Time.deltaTime;
        }

        private Vector3 GetMuzzleOrigin()
        {
            if (muzzle != null) return muzzle.position;
            return transform.position + Vector3.up * 1f + transform.forward * 0.7f;
        }

        private Vector3 GetAimDirection(float yaw)
        {
            return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        }

        /// <summary>
        /// §4.5: los VFX/SFX no afectan las reglas del juego, asi que se
        /// resuelven localmente y no viajan por la red.
        /// </summary>
        private void PlayLocalMuzzleFlash()
        {
            if (muzzle == null) return;

            GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flash.name = "MuzzleFlash_Local";
            flash.transform.SetParent(muzzle, false);
            flash.transform.localScale = Vector3.one * 0.22f;

            var collider = flash.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var renderer = flash.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = Character.color;

            Destroy(flash, 0.05f);
        }

        // ---------------- Vida, dano y reanimacion ----------------

        /// <summary>Llamado solo por el host. Devuelve true si el golpe mato al jugador.</summary>
        public bool ServerApplyDamage(float amount)
        {
            if (!IsServer || IsDowned.Value) return false;

            float reduced = amount * (1f - Mathf.Clamp(DamageReduction.Value, 0f, 0.85f));
            float remaining = Health.Value - reduced;

            if (remaining > 0f)
            {
                Health.Value = remaining;
                return false;
            }

            Health.Value = 0f;
            IsDowned.Value = true;
            Deaths.Value++;
            _downedTimer = downedDuration;

            CheckTeamWipe();
            return true;
        }

        public void ServerHeal(float amount)
        {
            if (!IsServer) return;
            Health.Value = Mathf.Min(MaxHealth.Value, Health.Value + amount);
        }

        /// <summary>Objeto "arma sobrecargada": mas dano hasta OverchargeUntil (hora del servidor).</summary>
        public void ServerGrantOvercharge(float seconds)
        {
            if (!IsServer) return;
            OverchargeUntil.Value = NetworkManager.ServerTime.Time + seconds;
        }

        /// <summary>Segundos de sobrecarga que quedan, usando la hora del servidor (igual en todos).</summary>
        public float OverchargeRemaining => IsSpawned
            ? Mathf.Max(0f, (float)(OverchargeUntil.Value - NetworkManager.ServerTime.Time))
            : 0f;

        public void ServerResetForRun()
        {
            if (!IsServer) return;

            float max = Character.maxHealth + BonusHealth.Value;
            MaxHealth.Value = max;
            Health.Value = max;
            IsDowned.Value = false;
            Kills.Value = 0;
            Deaths.Value = 0;
            OverchargeUntil.Value = 0;
            _downedTimer = 0f;
        }

        /// <summary>
        /// Reanimacion cooperativa: un jugador caido vuelve a la mitad de su
        /// vida si queda alguien de pie. Si caen todos a la vez, la run se
        /// resuelve por derrota.
        /// </summary>
        private void UpdateServerDowned()
        {
            if (!RunControllerActive()) return;

            // Con equipo, solo te levanta un companero que siga de pie. En
            // solitario no hay nadie que pueda hacerlo, asi que el jugador se
            // reincorpora solo al agotarse el tiempo de caida: sin esto una
            // partida de un jugador terminaria al primer golpe letal.
            bool solo = All.Count <= 1;
            if (!solo && !AnyTeammateAlive()) return;

            _downedTimer -= Time.deltaTime;
            if (_downedTimer <= 0f)
            {
                IsDowned.Value = false;
                Health.Value = Mathf.Max(1f, MaxHealth.Value * reviveHealthFraction);
            }
        }

        private bool AnyTeammateAlive()
        {
            for (int i = 0; i < All.Count; i++)
            {
                NetPlayer other = All[i];
                if (other == null || other == this) continue;
                if (!other.IsDowned.Value) return true;
            }
            return false;
        }

        private void CheckTeamWipe()
        {
            // "Wipe" es un concepto de equipo: con un solo jugador no existe.
            // La run sigue y el jugador se reincorpora al terminar el downed.
            if (All.Count <= 1) return;

            for (int i = 0; i < All.Count; i++)
            {
                if (All[i] != null && !All[i].IsDowned.Value) return;
            }

            RunController run = RunController.Instance;
            if (run != null && run.IsServer && run.CurrentPhase == RunPhase.Playing)
            {
                run.ServerEndRun(false);
            }
        }

        private static bool RunControllerActive()
        {
            RunController run = RunController.Instance;
            return run != null && run.IsServer && run.CurrentPhase == RunPhase.Playing;
        }

        public void ServerRegisterKill()
        {
            if (!IsServer) return;
            Kills.Value++;
        }

        // ---------------- Teletransporte (spawn por sala, §4.2) ----------------

        public void ServerTeleport(Vector3 position, float yaw)
        {
            if (!IsServer) return;

            // [RED] La posicion es del dueno, asi que el host le pide que se mueva.
            // El salto es legitimo: se acepta como nueva referencia de la reconciliacion.
            _serverAccepted = position;
            _serverAcceptedTime = Time.time;
            _serverIgnoreUntil = Time.time + 0.5f;
            TeleportClientRpc(position, yaw);
        }

        [ClientRpc]
        private void TeleportClientRpc(Vector3 position, float yaw)
        {
            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _interpPosition = position;
            _interpYaw = yaw;

            if (IsOwner) PublishPosition(yaw);
            else _teleportHoldUntil = Time.time + 0.5f;
        }

        // ---------------- Entrada ----------------

        private void ReadInput(out Vector3 move, out float yaw, out bool dash, out bool fire)
        {
            move = Vector3.zero;
            yaw = transform.eulerAngles.y;
            dash = false;
            fire = false;

#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed) move += Vector3.forward;
                if (keyboard.sKey.isPressed) move += Vector3.back;
                if (keyboard.aKey.isPressed) move += Vector3.left;
                if (keyboard.dKey.isPressed) move += Vector3.right;

                if (keyboard.spaceKey.wasPressedThisFrame && _dashCooldownRemaining <= 0f) dash = true;
                if (keyboard.eKey.isPressed) fire = true;
            }

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                Vector2 stick = gamepad.leftStick.ReadValue();
                move += new Vector3(stick.x, 0f, stick.y);

                Vector2 aim = gamepad.rightStick.ReadValue();
                if (aim.sqrMagnitude > 0.04f) yaw = Mathf.Atan2(aim.x, aim.y) * Mathf.Rad2Deg;

                if (gamepad.buttonSouth.wasPressedThisFrame && _dashCooldownRemaining <= 0f) dash = true;
                if (gamepad.rightTrigger.isPressed) fire = true;
            }

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                yaw = MouseAimYaw();

                // El boton izquierdo dispara; se mantiene pulsado para fuego continuo.
                if (mouse.leftButton.isPressed) fire = true;
                if (mouse.rightButton.wasPressedThisFrame && _dashCooldownRemaining <= 0f) dash = true;
            }
#endif
        }

        /// <summary>
        /// Apuntado con raton: se proyecta el cursor sobre el plano horizontal
        /// del jugador. Se resuelve en el cliente porque solo afecta a su propio
        /// apuntado.
        /// </summary>
        private float MouseAimYaw()
        {
#if ENABLE_INPUT_SYSTEM
            Camera cam = Camera.main;
            if (cam == null || Mouse.current == null) return transform.eulerAngles.y;

            Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            Plane ground = new Plane(Vector3.up, new Vector3(0f, transform.position.y, 0f));

            if (ground.Raycast(ray, out float enter))
            {
                Vector3 point = ray.GetPoint(enter);
                Vector3 direction = point - transform.position;
                direction.y = 0f;

                if (direction.sqrMagnitude > 0.01f)
                    return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }
#endif
            return transform.eulerAngles.y;
        }

        // ---------------- Presentacion ----------------

        private void UpdateBodyColor()
        {
            if (bodyRenderer == null) return;

            Color target = Character.color;
            if (IsDowned.Value) target = new Color(0.25f, 0.25f, 0.3f);

            // El jugador local se marca con mas brillo para no perderse en el grupo.
            if (IsOwner) target *= 1.25f;
            target.a = 1f;

            bodyRenderer.material.color = Color.Lerp(bodyRenderer.material.color, target, Time.deltaTime * 8f);
        }

        private void LateUpdate()
        {
            if (IsServer) UpdateDownedCooldown();
        }
    }
}
