using Unity.Netcode;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Jefe final del piso.
    ///
    /// §4.2 "Escalado por dificultad": su vida y su agresividad se ajustan a la
    /// cantidad de jugadores presentes al momento del enfrentamiento. La vida la
    /// calcula <see cref="RunController.ServerComputeBossMaxHealth"/> y la
    /// agresividad se traduce en cantidad de proyectiles por andanada y en la
    /// frecuencia de las mismas.
    ///
    /// Su muerte resuelve la partida por victoria (§2).
    /// </summary>
    public class NetBoss : NetworkBehaviour
    {
        public static NetBoss Current { get; private set; }

        [Header("Referencias")]
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private Transform fireOrigin;

        [Header("Combate")]
        [Tooltip("Distancia a la que el jefe intenta quedarse de su objetivo.")]
        [SerializeField] private float preferredDistance = 9f;

        [SerializeField] private float moveSpeed = 3.2f;
        [SerializeField] private float volleyInterval = 2.6f;
        [SerializeField] private float projectileDamage = 14f;
        [SerializeField] private float projectileSpeed = 12f;
        [SerializeField] private float projectileRange = 26f;

        [Tooltip("Radio de impacto contra el que se comprueban los disparos de los jugadores.")]
        [SerializeField] private float hitRadius = 1.8f;

        public readonly NetworkVariable<float> Health = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> MaxHealth = new NetworkVariable<float>(
            100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<Vector3> NetPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> NetYaw = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Cantidad de jugadores del enfrentamiento: sube proyectiles y cadencia.</summary>
        public readonly NetworkVariable<int> Aggression = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Fase visual: 0 = entero, 1 = danado, 2 = critico.</summary>
        public readonly NetworkVariable<int> PhaseIndex = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private float _volleyTimer;
        private float _strafeDirection = 1f;
        private Vector3 _interpPosition;
        private float _interpYaw;
        private bool _defeatAnnounced;

        public bool IsAlive => Health.Value > 0f;
        public float HitRadius => hitRadius;

        private void Awake()
        {
            if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<Renderer>();
        }

        public override void OnNetworkSpawn()
        {
            Current = this;
            _interpPosition = transform.position;
            _interpYaw = transform.eulerAngles.y;

            if (IsServer)
            {
                // El host decide vida y agresividad segun jugadores y dificultad.
                int maxHealth = RunController.Instance != null
                    ? RunController.Instance.ServerComputeBossMaxHealth()
                    : Mathf.RoundToInt(MaxHealth.Value);

                MaxHealth.Value = maxHealth;
                Health.Value = maxHealth;

                if (RunController.Instance != null)
                {
                    Aggression.Value = RunController.Instance.BossMaxAggression.Value;
                    RunController.Instance.ServerNotifyBossSpawned(maxHealth);
                }

                NetPosition.Value = transform.position;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Current == this) Current = null;
        }

        private void Update()
        {
            if (IsServer && IsAlive) UpdateServerAI();
            else if (!IsServer) UpdateClientInterpolation();

            UpdateVisuals();
        }

        // ---------------- IA autoritativa ----------------

        private void UpdateServerAI()
        {
            RunController run = RunController.Instance;
            if (run == null || run.CurrentPhase != RunPhase.Playing) return;

            NetPlayer target = FindNearestPlayer();
            if (target == null) return;

            Vector3 offset = target.transform.position - transform.position;
            offset.y = 0f;

            float distance = offset.magnitude;
            Vector3 toTarget = distance > 0.001f ? offset / distance : Vector3.zero;

            Vector3 right = new Vector3(toTarget.z, 0f, -toTarget.x);

            // Se mantiene a distancia media y orbita: obliga a moverse al grupo.
            float approach = Mathf.Clamp(distance - preferredDistance, -1f, 1f);
            Vector3 movement = toTarget * approach + right * _strafeDirection * 0.6f;

            if (movement.sqrMagnitude > 0.001f)
                transform.position += movement.normalized * moveSpeed * Time.deltaTime;

            if (Random.value < 0.01f) _strafeDirection *= -1f;

            float yaw = toTarget.sqrMagnitude > 0.001f
                ? Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg
                : transform.eulerAngles.y;

            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            _volleyTimer -= Time.deltaTime;
            if (_volleyTimer <= 0f)
            {
                _volleyTimer = ResolveVolleyInterval();
                FireVolley(ProjectileCount());
            }

            NetPosition.Value = transform.position;
            NetYaw.Value = yaw;
            PhaseIndex.Value = ResolvePhase();
        }

        /// <summary>Mas jugadores => mas proyectiles por andanada.</summary>
        private int ProjectileCount()
        {
            int baseCount = 8;
            return Mathf.Clamp(baseCount + (Aggression.Value - 1) * 4, 6, 24);
        }

        private float ResolveVolleyInterval()
        {
            // Con mas jugadores dispara mas seguido, con un suelo para que siga
            // siendo esquivable.
            float interval = volleyInterval - (Aggression.Value - 1) * 0.4f;
            return Mathf.Max(1.1f, interval);
        }

        private int ResolvePhase()
        {
            float t = MaxHealth.Value > 0f ? Health.Value / MaxHealth.Value : 0f;
            if (t > 0.66f) return 0;
            if (t > 0.33f) return 1;
            return 2;
        }

        private void FireVolley(int count)
        {
            if (projectilePrefab == null) return;

            Vector3 origin = fireOrigin != null ? fireOrigin.position : transform.position + Vector3.up * 1.5f;

            // En fase critica la andanada gira, para que no sea siempre el mismo patron.
            float offsetAngle = PhaseIndex.Value == 2 ? Random.Range(0f, 360f) : 0f;

            for (int i = 0; i < count; i++)
            {
                float angle = offsetAngle + i * (360f / count);
                Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

                GameObject instance = Instantiate(projectilePrefab, origin, Quaternion.LookRotation(direction));
                var projectile = instance.GetComponent<NetProjectile>();

                if (projectile == null)
                {
                    Destroy(instance);
                    continue;
                }

                projectile.ServerLaunch(direction, projectileDamage, projectileSpeed, projectileRange,
                    NetProjectile.Team.Enemies, NetworkManager.ServerClientId);
            }
        }

        private NetPlayer FindNearestPlayer()
        {
            NetPlayer best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null || !player.IsAlive) continue;

                float distance = (player.transform.position - transform.position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }

            return best;
        }

        private void UpdateClientInterpolation()
        {
            const float smoothing = 12f;
            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

            _interpPosition = Vector3.Lerp(_interpPosition, NetPosition.Value, t);
            _interpYaw = Mathf.LerpAngle(_interpYaw, NetYaw.Value, t);

            transform.position = _interpPosition;
            transform.rotation = Quaternion.Euler(0f, _interpYaw, 0f);
        }

        // ---------------- Dano y muerte ----------------

        public bool ServerApplyDamage(float amount, ulong sourceClientId)
        {
            if (!IsServer || !IsAlive) return false;

            Health.Value = Mathf.Max(0f, Health.Value - amount);

            RunController run = RunController.Instance;
            if (run != null) run.ServerSetBossHealth(Mathf.RoundToInt(Health.Value));

            if (Health.Value > 0f) return false;

            ServerDie(sourceClientId);
            return true;
        }

        private void ServerDie(ulong sourceClientId)
        {
            if (_defeatAnnounced) return;
            _defeatAnnounced = true;

            RunController run = RunController.Instance;
            if (run == null) return;

            NetPlayer killer = null;
            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                if (NetPlayer.All[i] != null && NetPlayer.All[i].OwnerClientId == sourceClientId)
                {
                    killer = NetPlayer.All[i];
                    break;
                }
            }

            if (killer != null) killer.ServerRegisterKill();

            // Bonus de cierre de run: el jefe vale mas que cualquier sala.
            run.ServerAddPoints(250);

            // §2: resolucion de la partida. La victoria la determina el host.
            run.ServerEndRun(true);

            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned) networkObject.Despawn(true);
        }

        // ---------------- Presentacion ----------------

        private void UpdateVisuals()
        {
            if (bodyRenderer == null) return;

            Color color;
            switch (PhaseIndex.Value)
            {
                case 1: color = new Color(1f, 0.55f, 0.15f); break;
                case 2: color = new Color(1f, 0.15f, 0.35f); break;
                default: color = new Color(0.65f, 0.25f, 1f); break;
            }

            bodyRenderer.material.color = Color.Lerp(bodyRenderer.material.color, color, Time.deltaTime * 6f);
        }
    }
}
