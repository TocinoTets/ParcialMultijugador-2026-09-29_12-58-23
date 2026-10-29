using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>Fases de la sesion (§2 "Core gameplay loop").</summary>
    public enum RunPhase
    {
        /// <summary>Seleccion de personaje, dificultad y ready-check.</summary>
        Lobby = 0,

        /// <summary>Run en curso: exploracion, combate y jefe.</summary>
        Playing = 1,

        /// <summary>Resumen final (victoria o derrota) y vuelta al hub.</summary>
        Results = 2
    }

    /// <summary>
    /// [RED] Estado autoritativo de la partida, propiedad del host.
    ///
    /// Concentra todo lo que en §4.5 se define como "estado compartido": la
    /// semilla de la mazmorra, la dificultad, el piso, los puntos del equipo y
    /// las salas ya superadas. Los clientes no deciden nada de esto: lo leen.
    ///
    /// La semilla es lo que hace que host y clientes construyan la MISMA
    /// mazmorra. El generador original usaba Random sin semilla, asi que cada
    /// maquina habria creado un piso distinto.
    /// </summary>
    public class RunController : NetworkBehaviour
    {
        public static RunController Instance { get; private set; }

        [Header("Balance")]
        [SerializeField] private int enemiesBasePerRoom = 3;
        [SerializeField] private int bossBaseHealth = 900;

        public readonly NetworkVariable<int> Seed = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> DifficultyIndex = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> Floor = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Valor de <see cref="RunPhase"/> como int (evita serializar enums en NetworkVariable).</summary>
        public readonly NetworkVariable<int> Phase = new NetworkVariable<int>(
            (int)RunPhase.Lobby, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> TeamPoints = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> RunWon = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> SeparateSpawns = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> BossMaxHealth = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> BossSpawned = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> BossHealth = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> BossMaxAggression = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>
        /// Salas superadas, codificadas como int para poder replicarlas.
        /// El estado de puertas de cada cliente se deriva de esta lista.
        /// </summary>
        public readonly NetworkList<int> ClearedRooms = new NetworkList<int>();

        public RunPhase CurrentPhase => (RunPhase)Phase.Value;
        public DifficultyDef Difficulty => GameData.GetDifficulty(DifficultyIndex.Value);

        /// <summary>Se dispara en todos los peers cuando cambia la fase.</summary>
        public event Action<RunPhase> PhaseChanged;

        /// <summary>Se dispara en todos los peers al superarse una sala (coordenada de grid).</summary>
        public event Action<Vector2Int> RoomCleared;

        private int _enemyKillStreak;

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;

            Phase.OnValueChanged += HandlePhaseChanged;
            ClearedRooms.OnListChanged += HandleClearedRoomsChanged;

            // Un cliente que entra a mitad de partida necesita ponerse al dia.
            PhaseChanged?.Invoke(CurrentPhase);
            foreach (int encoded in ClearedRooms) RoomCleared?.Invoke(Decode(encoded));
        }

        public override void OnNetworkDespawn()
        {
            Phase.OnValueChanged -= HandlePhaseChanged;
            ClearedRooms.OnListChanged -= HandleClearedRoomsChanged;
            if (Instance == this) Instance = null;
        }

        private void HandlePhaseChanged(int previous, int current)
        {
            PhaseChanged?.Invoke((RunPhase)current);
        }

        private void HandleClearedRoomsChanged(NetworkListEvent<int> evt)
        {
            if (evt.Type != NetworkListEvent<int>.EventType.Add) return;
            RoomCleared?.Invoke(Decode(evt.Value));
        }

        // ---------------- Codificacion de coordenadas ----------------

        private const int CoordBias = 4096;

        public static int Encode(Vector2Int coord)
        {
            return (coord.x + CoordBias) * (CoordBias * 2) + (coord.y + CoordBias);
        }

        public static Vector2Int Decode(int encoded)
        {
            int y = encoded % (CoordBias * 2) - CoordBias;
            int x = encoded / (CoordBias * 2) - CoordBias;
            return new Vector2Int(x, y);
        }

        // ---------------- Autoridad del host ----------------

        /// <summary>
        /// §2: la partida arranca aqui. La semilla se decide en el host y se
        /// replica, de modo que todos construyan el mismo piso.
        /// </summary>
        public void ServerBeginRun(int difficultyIndex, bool separateSpawns, int seed)
        {
            // [RED] La semilla viaja a todos: con ella cada peer genera la misma mazmorra.
            if (!IsServer) return;

            DifficultyIndex.Value = Mathf.Clamp(difficultyIndex, 0, GameData.Difficulties.Length - 1);
            SeparateSpawns.Value = separateSpawns;
            Seed.Value = seed;
            Floor.Value = 1;
            TeamPoints.Value = 0;
            RunWon.Value = false;
            BossSpawned.Value = false;
            BossHealth.Value = 0;
            BossMaxHealth.Value = 0;

            ClearedRooms.Clear();

            BossMaxAggression.Value = ServerComputeBossAggression();

            Phase.Value = (int)RunPhase.Playing;
        }

        public void ServerAddPoints(int amount)
        {
            // Con la run terminada los puntos ya se guardaron en el perfil: un disparo
            // que llega tarde no debe cambiar el resumen.
            if (!IsServer || amount == 0 || CurrentPhase != RunPhase.Playing) return;
            TeamPoints.Value = Mathf.Max(0, TeamPoints.Value + Mathf.RoundToInt(amount * Difficulty.pointsMul));
        }

        public void ServerMarkRoomCleared(Vector2Int coord)
        {
            if (!IsServer) return;

            int encoded = Encode(coord);
            if (!ClearedRooms.Contains(encoded)) ClearedRooms.Add(encoded);
        }

        /// <summary>
        /// §4.2: el jefe ajusta su vida y agresividad segun la cantidad de
        /// jugadores presentes al momento del enfrentamiento.
        /// </summary>
        public int ServerComputeBossMaxHealth()
        {
            int players = Mathf.Max(1, ConnectedPlayerCount());

            // Escalado sublineal: 3 jugadores no deben enfrentar 3x la vida,
            // porque el dano del grupo tampoco escala perfecto (espacio, apuntado).
            float playerFactor = 1f + (players - 1) * 0.65f;

            return Mathf.RoundToInt(bossBaseHealth * playerFactor * Difficulty.bossHealthMul);
        }

        private int ServerComputeBossAggression()
        {
            int players = Mathf.Max(1, ConnectedPlayerCount());
            return Mathf.Clamp(players, 1, GameData.MaxPlayers);
        }

        public void ServerNotifyBossSpawned(int maxHealth)
        {
            if (!IsServer) return;
            BossMaxHealth.Value = maxHealth;
            BossHealth.Value = maxHealth;
            BossSpawned.Value = true;
            BossMaxAggression.Value = ServerComputeBossAggression();
        }

        public void ServerSetBossHealth(int health)
        {
            if (!IsServer) return;
            BossHealth.Value = Mathf.Clamp(health, 0, Mathf.Max(1, BossMaxHealth.Value));
        }

        /// <summary>§2: resolucion de la partida por victoria.</summary>
        public void ServerEndRun(bool won)
        {
            if (!IsServer) return;
            RunWon.Value = won;
            Phase.Value = (int)RunPhase.Results;
        }

        /// <summary>Vuelve al lobby sin cerrar la sesion (§4.2: el lobby no se cierra al terminar la run).</summary>
        public void ServerReturnToLobby()
        {
            if (!IsServer) return;
            _enemyKillStreak = 0;
            Phase.Value = (int)RunPhase.Lobby;
        }

        public int ConnectedPlayerCount()
        {
            if (NetworkManager == null) return 1;
            return Mathf.Max(1, NetworkManager.ConnectedClientsIds.Count);
        }

        /// <summary>Enemigos que debe generar una sala normal, segun dificultad y jugadores.</summary>
        public int ServerEnemiesForRoom()
        {
            int players = ConnectedPlayerCount();
            float scaled = enemiesBasePerRoom + (players - 1) * 1.2f + DifficultyIndex.Value * 0.75f;
            return Mathf.Clamp(Mathf.RoundToInt(scaled), 1, 10);
        }

        public void ServerOnEnemyKilled(int points)
        {
            if (!IsServer) return;
            _enemyKillStreak++;
            ServerAddPoints(points);
        }

        public int EnemyKillStreak => _enemyKillStreak;
    }
}
