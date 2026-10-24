using UnityEngine;

namespace ProtocolExecution
{
    /// <summary>
    /// TEMPORAL (prueba automatica). Con "-autojoin" en la linea de comandos el
    /// build se une a 127.0.0.1 al arrancar y escribe su estado cada segundo.
    /// </summary>
    public class TestAutoLaunch : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-autojoin") < 0) return;
            new GameObject("TestAutoLaunch").AddComponent<TestAutoLaunch>();
        }

        private float _next;
        private bool _joined;

        private void Update()
        {
            if (!_joined && NetworkLauncher.Instance != null)
            {
                _joined = true;
                Debug.Log($"[TEST] Join -> {NetworkLauncher.Instance.Join("127.0.0.1")}");
            }

            if (Time.time < _next) return;
            _next = Time.time + 1f;

            RunController run = RunController.Instance;
            string local = "none";
            foreach (NetPlayer p in NetPlayer.All)
                if (p != null && p.IsOwner)
                    local = $"pos={p.transform.position} hp={p.Health.Value}/{p.MaxHealth.Value}";

            Debug.Log($"[TEST] running={NetworkLauncher.Instance?.IsRunning} " +
                      $"phase={(run != null && run.IsSpawned ? run.CurrentPhase.ToString() : "-")} " +
                      $"diff={(run != null && run.IsSpawned ? run.DifficultyIndex.Value : -1)} " +
                      $"players={NetPlayer.All.Count} enemies={NetEnemy.All.Count} " +
                      $"cleared={(run != null && run.IsSpawned ? run.ClearedRooms.Count : -1)} " +
                      $"local[{local}] meta={MetaProgression.Points} " +
                      $"reject='{NetworkLauncher.LastRejectReason}'");
        }
    }
}
