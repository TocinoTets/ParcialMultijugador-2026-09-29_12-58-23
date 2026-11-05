using System;
using System.IO;
using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// Persistencia del progreso del jugador (§5 "Persistencia de datos").
    ///
    /// El documento pide persistencia centralizada en base de datos para el
    /// progreso. En esta vertical slice se implementa la mitad que no depende
    /// de un backend: perfil local en JSON dentro de Application.persistentDataPath.
    /// El punto de sustitucion es esta clase: cuando exista el servicio
    /// (Cloud Save o backend propio) solo hay que reemplazar Load/Save por
    /// llamadas asincronas al servicio, manteniendo la misma API publica.
    /// </summary>
    public static class MetaProgression
    {
        [Serializable]
        private class Profile
        {
            public int version = 1;
            public int points;
            public int[] upgradeLevels;
            public bool[] unlockedCharacters;
            public int runsPlayed;
            public int runsWon;
            public int bossesKilled;
            public int totalPointsEarned;
        }

        private const string FolderName = "ProtocolExecution";
        private const string FileName = "profile.json";

        private static Profile _profile;

        public static event Action Changed;

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, FolderName, FileName);

        public static int Points => Ensure().points;
        public static int RunsPlayed => Ensure().runsPlayed;
        public static int RunsWon => Ensure().runsWon;
        public static int BossesKilled => Ensure().bossesKilled;
        public static int TotalPointsEarned => Ensure().totalPointsEarned;

        private static Profile Ensure()
        {
            if (_profile == null) Load();
            return _profile;
        }

        public static void Load()
        {
            _profile = null;

            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    _profile = JsonUtility.FromJson<Profile>(json);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MetaProgression] No se pudo leer el perfil, se crea uno nuevo: {e.Message}");
            }

            if (_profile == null) _profile = new Profile();

            // Normaliza estructuras por si el archivo venia de una version anterior.
            if (_profile.upgradeLevels == null || _profile.upgradeLevels.Length != GameData.Upgrades.Length)
                _profile.upgradeLevels = new int[GameData.Upgrades.Length];

            if (_profile.unlockedCharacters == null || _profile.unlockedCharacters.Length != GameData.Characters.Length)
            {
                _profile.unlockedCharacters = new bool[GameData.Characters.Length];
                // El primer personaje siempre esta disponible; el resto se
                // desbloquea con puntos y queda persistido.
                _profile.unlockedCharacters[0] = true;
            }

            if (!_profile.unlockedCharacters[0]) _profile.unlockedCharacters[0] = true;
        }

        public static void Save()
        {
            if (_profile == null) return;

            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonUtility.ToJson(_profile, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[MetaProgression] No se pudo guardar el perfil: {e.Message}");
            }
        }

        // ---------------- Puntos y partidas ----------------

        public static void AddPoints(int amount)
        {
            if (amount <= 0) return;
            Profile p = Ensure();
            p.points += amount;
            p.totalPointsEarned += amount;
            Save();
            Changed?.Invoke();
        }

        public static void RecordRun(bool won, int pointsEarned, bool bossKilled)
        {
            Profile p = Ensure();
            p.runsPlayed++;
            if (won) p.runsWon++;
            if (bossKilled) p.bossesKilled++;
            p.points += Mathf.Max(0, pointsEarned);
            p.totalPointsEarned += Mathf.Max(0, pointsEarned);
            Save();
            Changed?.Invoke();
        }

        // ---------------- Mejoras ----------------

        public static int GetLevel(string upgradeId)
        {
            int index = UpgradeIndex(upgradeId);
            return index < 0 ? 0 : Ensure().upgradeLevels[index];
        }

        public static bool IsMaxed(string upgradeId)
        {
            UpgradeDef def = GameData.GetUpgrade(upgradeId);
            return def != null && GetLevel(upgradeId) >= def.maxLevel;
        }

        public static int GetNextCost(string upgradeId)
        {
            UpgradeDef def = GameData.GetUpgrade(upgradeId);
            if (def == null || IsMaxed(upgradeId)) return -1;
            return GameData.GetUpgradeCost(def, GetLevel(upgradeId));
        }

        public static bool CanBuy(string upgradeId)
        {
            int cost = GetNextCost(upgradeId);
            return cost >= 0 && Ensure().points >= cost;
        }

        public static bool TryBuy(string upgradeId)
        {
            int cost = GetNextCost(upgradeId);
            if (cost < 0) return false;

            Profile p = Ensure();
            if (p.points < cost) return false;

            p.points -= cost;
            p.upgradeLevels[UpgradeIndex(upgradeId)]++;
            Save();
            Changed?.Invoke();
            return true;
        }

        private static int UpgradeIndex(string upgradeId)
        {
            for (int i = 0; i < GameData.Upgrades.Length; i++)
            {
                if (GameData.Upgrades[i].id == upgradeId) return i;
            }
            return -1;
        }

        // ---------------- Personajes ----------------

        public static bool IsCharacterUnlocked(int index)
        {
            Profile p = Ensure();
            return index >= 0 && index < p.unlockedCharacters.Length && p.unlockedCharacters[index];
        }

        public static int GetCharacterUnlockCost(int index)
        {
            if (index <= 0) return 0;
            return index * 150;
        }

        public static bool TryUnlockCharacter(int index)
        {
            Profile p = Ensure();
            if (index <= 0 || index >= p.unlockedCharacters.Length) return false;
            if (p.unlockedCharacters[index]) return false;

            int cost = GetCharacterUnlockCost(index);
            if (p.points < cost) return false;

            p.points -= cost;
            p.unlockedCharacters[index] = true;
            Save();
            Changed?.Invoke();
            return true;
        }

        // ---------------- Bonificaciones aplicadas en partida ----------------

        public static float BonusHealth => GetLevel("vitality") * GameData.GetUpgrade("vitality").perLevel;
        public static float BonusDamagePercent => GetLevel("damage") * GameData.GetUpgrade("damage").perLevel;
        public static float BonusSpeedPercent => GetLevel("stamina") * GameData.GetUpgrade("stamina").perLevel;

        /// <summary>Fraccion de dano recibido que se anula (0 a 0.85).</summary>
        public static float DamageReduction =>
            Mathf.Min(0.85f, GetLevel("defense") * GameData.GetUpgrade("defense").perLevel);

        /// <summary>Vida final de un personaje con las mejoras permanentes aplicadas.</summary>
        public static float ResolveMaxHealth(int characterIndex)
        {
            return GameData.GetCharacter(characterIndex).maxHealth + BonusHealth;
        }

        public static float ResolveDamage(int characterIndex)
        {
            return GameData.GetCharacter(characterIndex).damage * (1f + BonusDamagePercent);
        }

        public static float ResolveSpeed(int characterIndex)
        {
            return GameData.GetCharacter(characterIndex).moveSpeed * (1f + BonusSpeedPercent);
        }

        public static void ResetProfile()
        {
            _profile = new Profile();
            Save();
            Changed?.Invoke();
        }
    }
}
