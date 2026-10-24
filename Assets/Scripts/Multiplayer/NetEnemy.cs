using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Enemigo de red. La IA se ejecuta solo en el host; los clientes reciben la
    /// posicion autoritativa y la interpolan (§4.5).
    ///
    /// Integracion con el sistema de mazmorra existente: al morir llama a
    /// <see cref="DungeonSystem.EnemyMarker.Die"/>, que es el gancho que
    /// <c>RoomController</c> usa para desbloquear las puertas de la sala.
    /// </summary>
    public class NetEnemy : NetworkBehaviour
    {
        public static readonly List<NetEnemy> All = new List<NetEnemy>();

        [Header("Referencias")]
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private DungeonSystem.EnemyMarker marker;

        [Header("Combate")]
        [SerializeField] private float attackRange = 1.6f;
        [SerializeField] private float attackCooldown = 1.1f;
        [SerializeField] private float contactStopDistance = 1.25f;

        [Header("Colision")]
        [Tooltip("Radio de la capsula con la que se resuelve el choque contra paredes.")]
        [SerializeField] private float radius = 0.45f;

        [Tooltip("Capas que bloquean el paso. Todo menos 'Combatants' (capa 8): los demas enemigos y los jugadores no frenan al enemigo.")]
        [SerializeField] private LayerMask wallMask = ~(1 << 8);

        public readonly NetworkVariable<float> Health = new NetworkVariable<float>(
            10f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> MaxHealth = new NetworkVariable<float>(
            10f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<Vector3> NetPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> NetYaw = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private float _damage;
        private float _speed;
        private int _points;
        private float _attackTimer;
        private ulong _lastDamagerClientId;
        private Vector3 _interpPosition;
        private float _interpYaw;

        public bool IsAlive => Health.Value > 0f;

        private void Awake()
        {
            if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<Renderer>();
            if (marker == null) marker = GetComponent<DungeonSystem.EnemyMarker>();
        }

        /// <summary>Configura al enemigo antes de publicarlo. Solo en el host.</summary>
        public void ServerInitialize(float maxHealth, float damage, float speed, int points)
        {
            MaxHealth.Value = maxHealth;
            Health.Value = maxHealth;
            _damage = damage;
            _speed = speed;
            _points = points;
            NetPosition.Value = transform.position;
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            _interpPosition = transform.position;
            _interpYaw = transform.eulerAngles.y;

            if (IsServer) NetPosition.Value = transform.position;

            ApplyVisualStyle();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
        }

        private void Update()
        {
            if (IsServer && IsAlive) UpdateServerAI();
            else if (!IsServer) UpdateClientVisual();

            UpdateBar();
        }

        // ---------------- IA autoritativa ----------------

        private void UpdateServerAI()
        {
            RunController run = RunController.Instance;
            if (run == null || run.CurrentPhase != RunPhase.Playing) return;

            NetPlayer target = FindNearestPlayer();
            if (target == null)
            {
                NetPosition.Value = transform.position;
                return;
            }

            Vector3 toTarget = target.transform.position - transform.position;
            toTarget.y = 0f;

            float distance = toTarget.magnitude;
            Vector3 direction = distance > 0.001f ? toTarget / distance : Vector3.zero;

            float yaw = direction.sqrMagnitude > 0.001f
                ? Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg
                : transform.eulerAngles.y;

            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            if (distance > contactStopDistance)
            {
                MoveWithWallCheck(direction, _speed * Time.deltaTime);
            }

            _attackTimer -= Time.deltaTime;
            if (distance <= attackRange && _attackTimer <= 0f)
            {
                _attackTimer = attackCooldown;
                target.ServerApplyDamage(_damage);
            }

            NetPosition.Value = transform.position;
            NetYaw.Value = yaw;
        }

        /// <summary>
        /// Avanza en la direccion dada recortando el desplazamiento si hay una
        /// pared delante. El prefab no tiene Rigidbody, asi que la fisica no
        /// resuelve el choque por si sola: se comprueba explicitamente con un
        /// SphereCast, igual que hace NetPlayer con su capsula.
        /// </summary>
        private void MoveWithWallCheck(Vector3 direction, float distance)
        {
            if (distance <= 0f) return;

            // Mismo origen que NetPlayer: el centro de la capsula (1.8 m de alto).
            Vector3 origin = transform.position + Vector3.up * 0.9f;

            if (Physics.SphereCast(origin, radius, direction, out RaycastHit hit, distance,
                    wallMask, QueryTriggerInteraction.Ignore))
            {
                transform.position += direction * Mathf.Max(0f, hit.distance - 0.02f);
                return;
            }

            transform.position += direction * distance;
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

        private void UpdateClientVisual()
        {
            const float smoothing = 14f;
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

            _lastDamagerClientId = sourceClientId;
            Health.Value -= amount;

            if (Health.Value > 0f) return false;

            Health.Value = 0f;
            ServerDie();
            return true;
        }

        private void ServerDie()
        {
            RunController run = RunController.Instance;
            if (run != null)
            {
                run.ServerOnEnemyKilled(_points);

                NetPlayer killer = FindPlayer(_lastDamagerClientId);
                if (killer != null) killer.ServerRegisterKill();
            }

            // Gancho del sistema de mazmorra: RoomController escucha esta muerte
            // para saber cuando no quedan enemigos y abrir las puertas.
            if (marker != null) marker.Die();

            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned) networkObject.Despawn(true);
        }

        private static NetPlayer FindPlayer(ulong clientId)
        {
            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player != null && player.OwnerClientId == clientId) return player;
            }
            return null;
        }

        // ---------------- Presentacion ----------------

        private void ApplyVisualStyle()
        {
            if (bodyRenderer != null) bodyRenderer.material.color = new Color(0.95f, 0.25f, 0.35f);
        }

        /// <summary>La barra de vida solo se dibuja cuando el enemigo esta tocado.</summary>
        private void UpdateBar()
        {
            if (bodyRenderer == null) return;

            float t = MaxHealth.Value > 0f ? Mathf.Clamp01(Health.Value / MaxHealth.Value) : 0f;
            Color hurt = Color.Lerp(new Color(1f, 0.85f, 0.85f), new Color(0.95f, 0.25f, 0.35f), t);
            bodyRenderer.material.color = Color.Lerp(bodyRenderer.material.color, hurt, Time.deltaTime * 10f);
        }
    }
}
