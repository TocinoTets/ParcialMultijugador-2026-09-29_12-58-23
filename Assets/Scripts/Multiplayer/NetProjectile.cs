using Unity.Netcode;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Proyectil de red. Lo instancia siempre el host (§4.4: el host es la
    /// autoridad del estado compartido) y se replica a los clientes.
    ///
    /// La deteccion de impacto se resuelve en el host con comprobaciones de
    /// distancia contra los combatientes registrados, y con un Raycast para las
    /// paredes. Los clientes solo reproducen el movimiento.
    /// </summary>
    public class NetProjectile : NetworkBehaviour
    {
        public enum Team
        {
            Players = 0,
            Enemies = 1
        }

        [SerializeField] private float hitRadius = 0.35f;
        [SerializeField] private float lifetime = 6f;
        [SerializeField] private LayerMask wallMask = ~0;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private TrailRenderer trail;

        private readonly NetworkVariable<Vector3> _direction = new NetworkVariable<Vector3>(
            Vector3.forward, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<Vector3> _netPosition = new NetworkVariable<Vector3>(
            Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _team = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private float _damage;
        private float _speed;
        private float _range;
        private float _travelled;
        private float _age;
        private ulong _ownerClientId;
        private Vector3 _interpPosition;

        private void Awake()
        {
            if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<Renderer>();
            if (trail == null) trail = GetComponentInChildren<TrailRenderer>();
        }

        public override void OnNetworkSpawn()
        {
            _interpPosition = transform.position;

            if (IsServer) _netPosition.Value = transform.position;

            ApplyVisualStyle();
        }

        /// <summary>Configura y publica el proyectil. Solo se llama en el host.</summary>
        public void ServerLaunch(Vector3 direction, float damage, float speed, float range, Team team, ulong ownerClientId)
        {
            _damage = damage;
            _speed = speed;
            _range = range;
            _ownerClientId = ownerClientId;

            _direction.Value = direction.normalized;
            _team.Value = (int)team;
            _netPosition.Value = transform.position;

            transform.rotation = Quaternion.LookRotation(direction);

            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null && !networkObject.IsSpawned) networkObject.Spawn(true);
        }

        private void Update()
        {
            if (IsServer) UpdateServer();
            else UpdateClient();
        }

        private void UpdateServer()
        {
            float dt = Time.deltaTime;
            _age += dt;

            Vector3 direction = _direction.Value;
            float step = _speed * dt;
            Vector3 origin = transform.position;

            // Pared / puerta cerrada.
            if (Physics.Raycast(origin, direction, out RaycastHit wallHit, step + hitRadius,
                    wallMask, QueryTriggerInteraction.Ignore))
            {
                Despawn();
                return;
            }

            Vector3 next = origin + direction * step;
            if (CheckHits(next))
            {
                Despawn();
                return;
            }

            transform.position = next;
            _netPosition.Value = next;
            _travelled += step;

            if (_travelled >= _range || _age >= lifetime) Despawn();
        }

        private bool CheckHits(Vector3 position)
        {
            Team team = (Team)_team.Value;

            if (team == Team.Players)
            {
                for (int i = 0; i < NetEnemy.All.Count; i++)
                {
                    NetEnemy enemy = NetEnemy.All[i];
                    if (enemy == null || !enemy.IsAlive) continue;
                    if (Vector3.Distance(Flatten(position), Flatten(enemy.transform.position)) > hitRadius + 0.5f) continue;

                    enemy.ServerApplyDamage(_damage, _ownerClientId);
                    return true;
                }

                // El jefe tambien es objetivo de los disparos de jugador.
                NetBoss boss = NetBoss.Current;
                if (boss != null && boss.IsAlive &&
                    Vector3.Distance(Flatten(position), Flatten(boss.transform.position)) <= hitRadius + boss.HitRadius)
                {
                    boss.ServerApplyDamage(_damage, _ownerClientId);
                    return true;
                }

                return false;
            }

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null || !player.IsAlive) continue;
                if (Vector3.Distance(Flatten(position), Flatten(player.transform.position)) > hitRadius + 0.5f) continue;

                player.ServerApplyDamage(_damage);
                return true;
            }

            return false;
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        /// <summary>
        /// §4.5: interpolacion. El proyectil se mueve en el host y aqui se
        /// suaviza entre los estados recibidos para que no salte.
        /// </summary>
        private void UpdateClient()
        {
            const float smoothing = 22f;
            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

            _interpPosition = Vector3.Lerp(_interpPosition, _netPosition.Value, t);
            transform.position = _interpPosition;

            Vector3 direction = _direction.Value;
            if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
        }

        private void Despawn()
        {
            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned) networkObject.Despawn(true);
            else Destroy(gameObject);
        }

        private void ApplyVisualStyle()
        {
            Color color = (Team)_team.Value == Team.Players
                ? new Color(0.35f, 0.95f, 1f)
                : new Color(1f, 0.3f, 0.35f);

            if (bodyRenderer != null) bodyRenderer.material.color = color;
            if (trail != null)
            {
                trail.startColor = color;
                trail.endColor = new Color(color.r, color.g, color.b, 0f);
            }
        }
    }
}
