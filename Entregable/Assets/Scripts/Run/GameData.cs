using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Dificultades de partida (§2 "Selección de la dificultad"). Cada una escala
    /// estadísticas de enemigos y jefe, y multiplica los puntos obtenidos.
    /// </summary>
    public enum GameDifficulty
    {
        Easy = 0,
        Normal = 1,
        Hard = 2
    }

    /// <summary>Personaje jugable (§2 "Selección del personaje").</summary>
    [System.Serializable]
    public class CharacterDef
    {
        public string id;
        public string displayName;
        public string description;

        public float maxHealth;
        public float moveSpeed;
        public float damage;

        [Tooltip("Disparos por segundo.")]
        public float fireRate;

        public float projectileSpeed;
        public float projectileRange;
        public float dashCooldown;

        public Color color;
    }

    /// <summary>Curva de dificultad de una partida.</summary>
    [System.Serializable]
    public class DifficultyDef
    {
        public GameDifficulty id;
        public string displayName;
        public string description;

        public float enemyHealthMul;
        public float enemyDamageMul;
        public float enemySpeedMul;
        public float bossHealthMul;
        public float pointsMul;
    }

    /// <summary>
    /// Mejora permanente comprada con puntos entre partidas (§2: "Los puntos
    /// recolectados se invierten en mejoras de las habilidades básicas
    /// compartidas por todos los personajes").
    /// </summary>
    [System.Serializable]
    public class UpgradeDef
    {
        public string id;
        public string displayName;
        public string description;
        public int baseCost;
        public int maxLevel;

        [Tooltip("Ganancia acumulada por nivel (unidades según la estadística).")]
        public float perLevel;
    }

    /// <summary>
    /// Tabla de datos estáticos del juego. Se define en código (no en
    /// ScriptableObjects) para que el proyecto funcione sin cablear assets:
    /// las estadísticas se leen siempre por índice.
    /// </summary>
    public static class GameData
    {
        public const int MaxPlayers = 3;

        public static readonly CharacterDef[] Characters =
        {
            new CharacterDef
            {
                id = "vex",
                displayName = "VEX-01",
                description = "Androide balanceado. Cadencia media, alcance estable.",
                maxHealth = 100f,
                moveSpeed = 6.5f,
                damage = 12f,
                fireRate = 3.5f,
                projectileSpeed = 22f,
                projectileRange = 16f,
                dashCooldown = 1.6f,
                color = new Color(0.20f, 0.95f, 1.00f)
            },
            new CharacterDef
            {
                id = "nyx",
                displayName = "NYX-07",
                description = "Especialista en infiltración. Rápida y frágil, dispara muy seguido.",
                maxHealth = 70f,
                moveSpeed = 8.2f,
                damage = 8f,
                fireRate = 6.0f,
                projectileSpeed = 26f,
                projectileRange = 14f,
                dashCooldown = 1.0f,
                color = new Color(1.00f, 0.25f, 0.75f)
            },
            new CharacterDef
            {
                id = "rook",
                displayName = "ROOK-3",
                description = "Unidad pesada. Mucha vida y daño alto, lenta y con cadencia baja.",
                maxHealth = 145f,
                moveSpeed = 5.2f,
                damage = 22f,
                fireRate = 2.0f,
                projectileSpeed = 18f,
                projectileRange = 12f,
                dashCooldown = 2.4f,
                color = new Color(1.00f, 0.75f, 0.15f)
            }
        };

        public static readonly DifficultyDef[] Difficulties =
        {
            new DifficultyDef
            {
                id = GameDifficulty.Easy,
                displayName = "RECON",
                description = "Enemigos con menos vida y daño. Ideal para reconocer el piso.",
                enemyHealthMul = 0.75f,
                enemyDamageMul = 0.70f,
                enemySpeedMul = 0.90f,
                bossHealthMul = 0.80f,
                pointsMul = 0.80f
            },
            new DifficultyDef
            {
                id = GameDifficulty.Normal,
                displayName = "PROTOCOLO",
                description = "Curva estándar. La experiencia prevista del juego.",
                enemyHealthMul = 1.00f,
                enemyDamageMul = 1.00f,
                enemySpeedMul = 1.00f,
                bossHealthMul = 1.00f,
                pointsMul = 1.00f
            },
            new DifficultyDef
            {
                id = GameDifficulty.Hard,
                displayName = "EJECUCION",
                description = "Enemigos duros, agresivos y rápidos. Puntos x1.5.",
                enemyHealthMul = 1.45f,
                enemyDamageMul = 1.40f,
                enemySpeedMul = 1.15f,
                bossHealthMul = 1.35f,
                pointsMul = 1.50f
            }
        };

        public static readonly UpgradeDef[] Upgrades =
        {
            new UpgradeDef
            {
                id = "vitality",
                displayName = "BLINDAJE",
                description = "+10 de vida maxima por nivel.",
                baseCost = 40,
                maxLevel = 10,
                perLevel = 10f
            },
            new UpgradeDef
            {
                id = "damage",
                displayName = "SOBRECARGA",
                description = "+8% de dano por nivel.",
                baseCost = 55,
                maxLevel = 10,
                perLevel = 0.08f
            },
            new UpgradeDef
            {
                id = "stamina",
                displayName = "SERVOMOTORES",
                description = "+4% de velocidad de movimiento por nivel.",
                baseCost = 45,
                maxLevel = 10,
                perLevel = 0.04f
            },
            new UpgradeDef
            {
                id = "defense",
                displayName = "NANOESCUDO",
                description = "-3% de dano recibido por nivel.",
                baseCost = 60,
                maxLevel = 10,
                perLevel = 0.03f
            }
        };

        public static CharacterDef GetCharacter(int index)
        {
            return Characters[Mathf.Clamp(index, 0, Characters.Length - 1)];
        }

        public static DifficultyDef GetDifficulty(int index)
        {
            return Difficulties[Mathf.Clamp(index, 0, Difficulties.Length - 1)];
        }

        public static UpgradeDef GetUpgrade(string id)
        {
            for (int i = 0; i < Upgrades.Length; i++)
            {
                if (Upgrades[i].id == id) return Upgrades[i];
            }
            return null;
        }

        /// <summary>Coste del siguiente nivel de una mejora (curva cuadratica suave).</summary>
        public static int GetUpgradeCost(UpgradeDef upgrade, int currentLevel)
        {
            return Mathf.RoundToInt(upgrade.baseCost * (1f + currentLevel * 0.75f));
        }
    }
}
