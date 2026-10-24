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
    /// Implementa las tecnicas pedidas en §4.5:
    ///
    ///  - Client-side prediction: el dueno del objeto aplica su propio input de
    ///    inmediato (movimiento, dash y disparo) sin esperar al host.
    ///  - Reconciliacion: el host aplica el mismo input sobre su copia y responde
    ///    con (posicion, tick). El cliente compara su posicion predicha para ese
    ///    tick y solo corrige si el error supera <see cref="reconcileThreshold"/>,
    ///    tal como pide el documento ("solo se fuerza una correccion de estado
    ///    cuando la diferencia afecta el resultado").
    ///  - Rollback + replay: cuando corrige, reposiciona y re-simula los inputs
    ///    pendientes para no perder los ultimos frames.
    ///  - Interpolacion: los jugadores remotos se interpolan hacia la ultima
    ///    posicion autoritativa en vez de saltar.
    ///
    /// El movimiento es cinematico y sin estado (posicion + input => nueva
    /// posicion), a proposito: es lo que hace que el replay en el cliente
    /// reproduzca exactamente lo que hizo el host.
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

        [Header("Reconciliacion (§4.5)")]
        [Tooltip("Error de posicion (m) a partir del cual se fuerza el rollback.")]
        [SerializeField] private float reconcileThreshold = 0.18f;

        [Tooltip("Cantidad de inputs que se conservan para poder re-simular.")]
        [SerializeField] private int inputHistorySize = 128;

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

        /// <summary>Posicion autoritativa del host. La consumen la reconciliacion y la interpolacion.</summary>
        public readonly NetworkVariable<Vector3> NetPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> NetYaw = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ---------------- Estado local ----------------

        private struct InputState
        {
            public uint tick;
            public Vector3 move;
            public float yaw;
            public bool dash;
            public bool fire;
            public float dt;
            public Vector3 predictedPosition;
        }

        private readonly List<InputState> _history = new List<InputState>();
        private uint _tick;
        private float _clientFireCooldown;
        private float _serverFireCooldown;
        private float _dashCooldownRemaining;
        private float _downedTimer;
        private Vector3 _interpPosition;
        private float _interpYaw;

        public bool IsAlive => !IsDowned.Value;
        public string DisplayName => PlayerName.Value.ToString();
        public CharacterDef Character => GameData.GetCharacter(CharacterIndex.Value);

        private float Speed => Character.moveSpeed * (1f + BonusSpeedPercent.Value);
        private float Damage => Character.damage * (1f + BonusDamagePercent.Value);

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
            }

            Health.OnValueChanged += HandleHealthChanged;
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            Health.OnValueChanged -= HandleHealthChanged;
        }

        private void HandleHealthChanged(float previous, float current)
        {
            // El dano recibido no se predice: es un evento raro y el cliente
            // debe ver el valor del host, no el suyo.
            if (IsOwner) _history.Clear();
        }

        // ---------------- Ciclo principal ----------------

        private void Update()
        {
            RunController run = RunController.Instance;
            bool runActive = run != null && run.CurrentPhase == RunPhase.Playing;

            if (runActive && !IsDowned.Value)
            {
                if (IsOwner) UpdateOwnerInput();

                if (IsServer) UpdateServerAuthority();
            }

            if (!IsServer && !IsOwner) UpdateRemoteInterpolation();
            else if (IsServer && !IsOwner) SyncAuthoritativeTransform();

            if (IsServer && IsDowned.Value) UpdateServerDowned();

            UpdateBodyColor();
        }

        // ---------------- Dueno del objeto: prediccion ----------------

        private void UpdateOwnerInput()
        {
            ReadInput(out Vector3 move, out float yaw, out bool dashRequested, out bool fireRequested);

            _tick++;

            // Prediccion: se aplica ya, sin esperar confirmacion del host.
            ApplyMovement(move, yaw, dashRequested, Time.deltaTime, out Vector3 newPosition);
            transform.position = newPosition;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            _history.Add(new InputState
            {
                tick = _tick,
                move = move,
                yaw = yaw,
                dash = dashRequested,
                fire = fireRequested,
                dt = Time.deltaTime,
                predictedPosition = newPosition
            });

            if (_history.Count > inputHistorySize) _history.RemoveAt(0);

            if (_dashCooldownRemaining > 0f) _dashCooldownRemaining -= Time.deltaTime;
            if (_clientFireCooldown > 0f) _clientFireCooldown -= Time.deltaTime;

            // En el host el movimiento local ya es el autoritativo: enviarse el
            // RPC a si mismo lo aplicaria dos veces (doble velocidad).
            if (!IsServer) SubmitInputServerRpc(move, yaw, dashRequested, Time.deltaTime, _tick);

            // §4.5: el disparo se resuelve en el host, pero el feedback visual y
            // sonoro se genera localmente para que se sienta inmediato.
            if (fireRequested && _clientFireCooldown <= 0f)
            {
                _clientFireCooldown = 1f / Mathf.Max(0.01f, Character.fireRate);
                PlayLocalMuzzleFlash();
                RequestFireServerRpc(GetMuzzleOrigin(), GetAimDirection(yaw));
            }
        }

        /// <summary>
        /// Movimiento sin estado: la misma entrada produce la misma salida en el
        /// host y en el cliente, que es lo que hace posible el rollback + replay.
        /// </summary>
        private void ApplyMovement(Vector3 move, float yaw, bool dash, float dt, out Vector3 newPosition,
            bool isReplay = false)
        {
            Vector3 direction = move.sqrMagnitude > 0.0001f ? move.normalized : Vector3.zero;

            // Durante el replay el dash ya venia validado cuando se envio el input,
            // asi que se reproduce tal cual en vez de volver a comprobar el cooldown.
            // Si no, la re-simulacion perderia el dash y divergiria del host.
            bool dashAllowed = direction != Vector3.zero && (isReplay || _dashCooldownRemaining <= 0f);

            Vector3 displacement;
            if (dash && dashAllowed)
            {
                displacement = direction * dashDistance;
                if (!isReplay) _dashCooldownRemaining = Character.dashCooldown;
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

        // ---------------- Host: simulacion autoritativa ----------------

        [ServerRpc]
        private void SubmitInputServerRpc(Vector3 move, float yaw, bool dash, float dt, uint tick,
            ServerRpcParams rpcParams = default)
        {
            ApplyMovement(move, yaw, dash, dt, out Vector3 newPosition);
            transform.position = newPosition;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            if (_dashCooldownRemaining > 0f) _dashCooldownRemaining -= dt;

            PublishAuthoritativeState(yaw);

            // Solo el dueno necesita la correccion: los demas interpolan.
            ulong sender = rpcParams.Receive.SenderClientId;
            if (sender != NetworkManager.ServerClientId)
            {
                SendCorrectionClientRpc(newPosition, yaw, tick,
                    new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } } });
            }
        }

        private void UpdateServerAuthority()
        {
            // El host tambien publica su propio estado para los clientes remotos.
            if (IsOwner) PublishAuthoritativeState(transform.eulerAngles.y);
        }

        private void PublishAuthoritativeState(float yaw)
        {
            NetPosition.Value = transform.position;
            NetYaw.Value = yaw;
        }

        private void SyncAuthoritativeTransform()
        {
            transform.position = NetPosition.Value;
            transform.rotation = Quaternion.Euler(0f, NetYaw.Value, 0f);
        }

        [ClientRpc]
        private void SendCorrectionClientRpc(Vector3 authoritativePosition, float yaw, uint tick,
            ClientRpcParams rpcParams = default)
        {
            if (!IsOwner || IsServer) return;

            int index = _history.FindIndex(h => h.tick == tick);
            if (index < 0) return;

            float error = Vector3.Distance(_history[index].predictedPosition, authoritativePosition);

            // §4.5: minimizar correcciones visibles. Si el error es bajo, el
            // estado predicho ya es suficientemente bueno y no se toca nada.
            if (error < reconcileThreshold) return;

            RollbackAndReplay(authoritativePosition, yaw, index);
        }

        /// <summary>Rollback + replay de los inputs que el host todavia no habia confirmado.</summary>
        private void RollbackAndReplay(Vector3 authoritativePosition, float yaw, int confirmedIndex)
        {
            transform.position = authoritativePosition;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _interpPosition = authoritativePosition;

            for (int i = confirmedIndex + 1; i < _history.Count; i++)
            {
                InputState state = _history[i];
                ApplyMovement(state.move, state.yaw, state.dash, state.dt, out Vector3 replayPosition, true);

                state.predictedPosition = replayPosition;
                _history[i] = state;
                transform.position = replayPosition;
            }
        }

        // ---------------- Clientes remotos: interpolacion ----------------

        private void UpdateRemoteInterpolation()
        {
            // §4.5: interpolacion entre estados del host para que el jitter no
            // produzca saltos bruscos.
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

        public void ServerResetForRun()
        {
            if (!IsServer) return;

            float max = Character.maxHealth + BonusHealth.Value;
            MaxHealth.Value = max;
            Health.Value = max;
            IsDowned.Value = false;
            Kills.Value = 0;
            Deaths.Value = 0;
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

            // Se actualiza tambien el estado autoritativo: si no, el host volveria
            // a poner al jugador en su NetPosition vieja y lo reconciliaria hacia atras.
            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            PublishAuthoritativeState(yaw);

            TeleportClientRpc(position, yaw);
        }

        [ClientRpc]
        private void TeleportClientRpc(Vector3 position, float yaw)
        {
            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _interpPosition = position;
            _interpYaw = yaw;
            _history.Clear();
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
