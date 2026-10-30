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

        private static async void JoinOnline(string code)
        {
            bool ok = await NetworkLauncher.Instance.JoinOnlineAsync(code);
            Debug.Log($"[TEST] JoinOnline {code} -> {ok} {NetworkLauncher.StatusMessage}");
        }

        private float _next;
        private bool _joined;

        private void Update()
        {
            if (!_joined && NetworkLauncher.Instance != null)
            {
                _joined = true;

                // "-port N": prueba en otro puerto si el 7777 quedo ocupado.
                string[] args = System.Environment.GetCommandLineArgs();
                int i = System.Array.IndexOf(args, "-port");
                if (i >= 0 && i + 1 < args.Length && ushort.TryParse(args[i + 1], out ushort port))
                    typeof(NetworkLauncher).GetField("port", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .SetValue(NetworkLauncher.Instance, port);

                // "-code ABC123": entra a la sala online; si no, LAN a 127.0.0.1.
                int c = System.Array.IndexOf(args, "-code");
                if (c >= 0 && c + 1 < args.Length) JoinOnline(args[c + 1]);
                else Debug.Log($"[TEST] Join -> {NetworkLauncher.Instance.Join("127.0.0.1")}");
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
                      $"players={NetPlayer.All.Count} enemies={NetEnemy.All.Count} pickups={FindObjectsByType<NetPickup>(FindObjectsSortMode.None).Length} " +
                      $"cleared={(run != null && run.IsSpawned ? run.ClearedRooms.Count : -1)} " +
                      $"local[{local}] meta={MetaProgression.Points} " +
                      $"reject='{NetworkLauncher.LastRejectReason}' code={NetworkLauncher.Instance?.SessionCode} status='{NetworkLauncher.StatusMessage}'");
        }
    }
}
