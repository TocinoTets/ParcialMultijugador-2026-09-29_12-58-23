#if UNITY_EDITOR
using System.Collections.Generic;
using DungeonSystem;
using ProtocolExecution;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Monta la escena jugable de "Protocol: Execution" por completo desde codigo.
///
/// Se puede volver a ejecutar en cualquier momento (es idempotente): recrea
/// materiales y prefabs, actualiza la lista de prefabs de red y recablea la
/// escena. Menu: Protocol Execution > Configurar escena jugable.
/// </summary>
public static class ProtocolSetup
{
    private const string PrefabFolder = "Assets/Prefabs";
    private const string MaterialFolder = "Assets/Materials";
    private const string PrefabsListPath = "Assets/NetworkPrefabsList.asset";
    private const string CombatantsLayerName = "Combatants";

    private const string PlayerPrefabPath = PrefabFolder + "/Player.prefab";
    private const string EnemyPrefabPath = PrefabFolder + "/Enemy.prefab";
    private const string BossPrefabPath = PrefabFolder + "/Boss.prefab";
    private const string ProjectilePrefabPath = PrefabFolder + "/Projectile.prefab";
    private const string GameFlowPrefabPath = PrefabFolder + "/GameFlow.prefab";

    public const ushort Port = 7777;

    [MenuItem("Protocol Execution/Configurar escena jugable")]
    public static void Configure()
    {
        // Configura la escena: en Play mode Unity no deja marcarla como modificada.
        if (EditorApplication.isPlaying) return;

        int combatantsLayer = EnsureLayer(CombatantsLayerName);
        if (combatantsLayer < 0)
        {
            Debug.LogError("[ProtocolSetup] No hay capas libres para crear 'Combatants'.");
            return;
        }

        EnsureFolder("Assets", "Prefabs");
        EnsureFolder("Assets", "Materials");

        // ---------------- Materiales ----------------

        Material playerMaterial = CreateMaterial(MaterialFolder + "/Cyber_Player.mat",
            new Color(0.85f, 0.92f, 1.00f), new Color(0.10f, 0.35f, 0.45f), 0.70f);
        Material enemyMaterial = CreateMaterial(MaterialFolder + "/Cyber_Enemy.mat",
            new Color(0.95f, 0.25f, 0.35f), new Color(0.45f, 0.03f, 0.08f), 0.55f);
        Material bossMaterial = CreateMaterial(MaterialFolder + "/Cyber_Boss.mat",
            new Color(0.65f, 0.25f, 1.00f), new Color(0.30f, 0.05f, 0.55f), 0.80f);
        Material projectileMaterial = CreateMaterial(MaterialFolder + "/Cyber_Projectile.mat",
            new Color(0.35f, 0.95f, 1.00f), new Color(0.20f, 0.80f, 1.00f), 0.90f);

        // ---------------- Prefabs ----------------

        GameObject projectilePrefab = BuildProjectilePrefab(combatantsLayer, projectileMaterial);
        GameObject playerPrefab = BuildPlayerPrefab(combatantsLayer, playerMaterial, projectilePrefab);
        GameObject enemyPrefab = BuildEnemyPrefab(combatantsLayer, enemyMaterial);
        GameObject bossPrefab = BuildBossPrefab(combatantsLayer, bossMaterial, projectilePrefab);
        GameObject gameFlowPrefab = BuildGameFlowPrefab();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // ---------------- Lista de prefabs de red ----------------

        NetworkPrefabsList prefabsList = CreatePrefabsList(
            playerPrefab, enemyPrefab, bossPrefab, projectilePrefab, gameFlowPrefab);

        // ---------------- Escena ----------------

        GameObject networkManagerObject = ConfigureNetworkManager(playerPrefab, prefabsList);
        ConfigureSystems(networkManagerObject, gameFlowPrefab, enemyPrefab, bossPrefab);
        EnsureBuildSettings();

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        Debug.Log("[ProtocolSetup] Escena configurada. Host: 'CREAR PARTIDA'. Clientes: ejecutar una segunda instancia y 'UNIRSE' a la IP del host.");
    }

    // ---------------- Capas y carpetas ----------------

    private static int EnsureLayer(string layerName)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0) return -1;

        var tagManager = new SerializedObject(assets[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");
        if (layers == null) return -1;

        for (int i = 8; i < layers.arraySize; i++)
        {
            if (layers.GetArrayElementAtIndex(i).stringValue == layerName) return i;
        }

        for (int i = 8; i < layers.arraySize; i++)
        {
            SerializedProperty slot = layers.GetArrayElementAtIndex(i);
            if (!string.IsNullOrEmpty(slot.stringValue)) continue;

            slot.stringValue = layerName;
            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return i;
        }

        return -1;
    }

    private static void EnsureFolder(string parent, string child)
    {
        if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
            AssetDatabase.CreateFolder(parent, child);
    }

    // ---------------- Materiales ----------------

    private static Material CreateMaterial(string path, Color baseColor, Color emission, float smoothness)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
        if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.35f);

        // El brillo emisivo es la parte "cyberpunk/distopica" de la direccion de arte.
        if (emission.maxColorComponent > 0.001f)
        {
            material.EnableKeyword("_EMISSION");
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", emission);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    // ---------------- Prefabs ----------------

    private static GameObject BuildVisual(PrimitiveType type, string name, Transform parent, Vector3 localPosition,
        Vector3 localScale, Material material, out Renderer renderer)
    {
        GameObject visual = GameObject.CreatePrimitive(type);
        visual.name = name;

        Collider collider = visual.GetComponent<Collider>();
        if (collider != null) Object.DestroyImmediate(collider);

        visual.transform.SetParent(parent, false);
        visual.transform.localPosition = localPosition;
        visual.transform.localScale = localScale;
        visual.layer = parent.gameObject.layer;

        renderer = visual.GetComponent<Renderer>();
        if (renderer != null && material != null) renderer.sharedMaterial = material;

        return visual;
    }

    private static GameObject BuildProjectilePrefab(int layer, Material material)
    {
        var root = new GameObject("Projectile");
        root.layer = layer;

        BuildVisual(PrimitiveType.Sphere, "Visual", root.transform, Vector3.zero,
            Vector3.one * 0.3f, material, out Renderer renderer);

        var trail = root.AddComponent<TrailRenderer>();
        trail.time = 0.28f;
        trail.startWidth = 0.22f;
        trail.endWidth = 0f;
        trail.material = material;
        trail.startColor = new Color(0.35f, 0.95f, 1f);
        trail.endColor = new Color(0.35f, 0.95f, 1f, 0f);
        trail.minVertexDistance = 0.05f;

        root.AddComponent<NetworkObject>();
        var projectile = root.AddComponent<NetProjectile>();

        var so = new SerializedObject(projectile);
        SetRef(so, "bodyRenderer", renderer);
        SetRef(so, "trail", trail);
        SetInt(so, "wallMask", ~(1 << layer));
        so.ApplyModifiedPropertiesWithoutUndo();

        return SavePrefab(root, ProjectilePrefabPath);
    }

    private static GameObject BuildPlayerPrefab(int layer, Material material, GameObject projectilePrefab)
    {
        var root = new GameObject("Player");
        root.tag = "Player";
        root.layer = layer;

        var capsule = root.AddComponent<CapsuleCollider>();
        capsule.center = new Vector3(0f, 0.9f, 0f);
        capsule.height = 1.8f;
        capsule.radius = 0.45f;

        // Rigidbody cinematico: el movimiento lo resuelve NetPlayer, pero la
        // fisica necesita un cuerpo para disparar OnTriggerEnter/Exit contra el
        // volumen de deteccion de cada sala.
        var rigidbody = root.AddComponent<Rigidbody>();
        rigidbody.isKinematic = true;
        rigidbody.useGravity = false;
        rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

        BuildVisual(PrimitiveType.Capsule, "Body", root.transform, new Vector3(0f, 0.9f, 0f),
            new Vector3(0.9f, 0.9f, 0.9f), material, out Renderer renderer);

        var muzzleTransform = new GameObject("Muzzle").transform;
        muzzleTransform.SetParent(root.transform, false);
        muzzleTransform.localPosition = new Vector3(0f, 1.1f, 0.55f);
        muzzleTransform.gameObject.layer = layer;

        root.AddComponent<NetworkObject>();
        var player = root.AddComponent<NetPlayer>();

        var so = new SerializedObject(player);
        SetRef(so, "projectilePrefab", projectilePrefab);
        SetRef(so, "muzzle", muzzleTransform);
        SetRef(so, "bodyRenderer", renderer);
        SetInt(so, "wallMask", ~(1 << layer));
        so.ApplyModifiedPropertiesWithoutUndo();

        return SavePrefab(root, PlayerPrefabPath);
    }

    private static GameObject BuildEnemyPrefab(int layer, Material material)
    {
        var root = new GameObject("Enemy");
        root.layer = layer;

        var capsule = root.AddComponent<CapsuleCollider>();
        capsule.center = new Vector3(0f, 0.9f, 0f);
        capsule.height = 1.8f;
        capsule.radius = 0.45f;

        BuildVisual(PrimitiveType.Capsule, "Body", root.transform, new Vector3(0f, 0.9f, 0f),
            new Vector3(0.85f, 0.85f, 0.85f), material, out Renderer renderer);

        var marker = root.AddComponent<DungeonSystem.EnemyMarker>();
        root.AddComponent<NetworkObject>();
        var enemy = root.AddComponent<NetEnemy>();

        var so = new SerializedObject(enemy);
        SetRef(so, "bodyRenderer", renderer);
        SetRef(so, "marker", marker);
        so.ApplyModifiedPropertiesWithoutUndo();

        return SavePrefab(root, EnemyPrefabPath);
    }

    private static GameObject BuildBossPrefab(int layer, Material material, GameObject projectilePrefab)
    {
        var root = new GameObject("Boss");
        root.layer = layer;

        var sphere = root.AddComponent<SphereCollider>();
        sphere.center = new Vector3(0f, 1.5f, 0f);
        sphere.radius = 1.6f;

        BuildVisual(PrimitiveType.Sphere, "Core", root.transform, new Vector3(0f, 1.5f, 0f),
            Vector3.one * 3.2f, material, out Renderer renderer);

        var fireOrigin = new GameObject("FireOrigin").transform;
        fireOrigin.SetParent(root.transform, false);
        fireOrigin.localPosition = new Vector3(0f, 1.5f, 0f);
        fireOrigin.gameObject.layer = layer;

        root.AddComponent<NetworkObject>();
        var boss = root.AddComponent<NetBoss>();

        var so = new SerializedObject(boss);
        SetRef(so, "projectilePrefab", projectilePrefab);
        SetRef(so, "bodyRenderer", renderer);
        SetRef(so, "fireOrigin", fireOrigin);
        so.ApplyModifiedPropertiesWithoutUndo();

        return SavePrefab(root, BossPrefabPath);
    }

    private static GameObject BuildGameFlowPrefab()
    {
        var root = new GameObject("GameFlow");
        root.AddComponent<NetworkObject>();
        root.AddComponent<RunController>();

        return SavePrefab(root, GameFlowPrefabPath);
    }

    private static GameObject SavePrefab(GameObject instance, string path)
    {
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
        Object.DestroyImmediate(instance);
        return prefab;
    }

    // ---------------- Red ----------------

    private static NetworkPrefabsList CreatePrefabsList(params GameObject[] prefabs)
    {
        AssetDatabase.DeleteAsset(PrefabsListPath);

        var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
        AssetDatabase.CreateAsset(list, PrefabsListPath);

        foreach (GameObject prefab in prefabs)
        {
            if (prefab == null) continue;
            list.Add(new NetworkPrefab { Prefab = prefab });
        }

        EditorUtility.SetDirty(list);
        AssetDatabase.SaveAssets();

        return list;
    }

    private static GameObject ConfigureNetworkManager(GameObject playerPrefab, NetworkPrefabsList prefabsList)
    {
        NetworkManager manager = Object.FindFirstObjectByType<NetworkManager>();
        GameObject managerObject;

        if (manager == null)
        {
            managerObject = new GameObject("NetworkManager");
            manager = managerObject.AddComponent<NetworkManager>();
        }
        else
        {
            managerObject = manager.gameObject;
        }

        UnityTransport transport = managerObject.GetComponent<UnityTransport>();
        if (transport == null) transport = managerObject.AddComponent<UnityTransport>();

        // NGO no auto-asigna el transporte al configurarlo por script: sin esta
        // linea StartHost() lanza "[Netcode] No transport has been selected!" y
        // la sesion nunca arranca (ni host ni cliente).
        manager.NetworkConfig.NetworkTransport = transport;

        transport.SetConnectionData("127.0.0.1", Port, "0.0.0.0");

        manager.NetworkConfig.PlayerPrefab = playerPrefab;
        manager.NetworkConfig.ConnectionApproval = true;
        manager.NetworkConfig.EnableSceneManagement = false;

        manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
        manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(prefabsList);

        EditorUtility.SetDirty(manager);
        EditorUtility.SetDirty(transport);

        return managerObject;
    }

    private static void ConfigureSystems(GameObject networkManagerObject, GameObject gameFlowPrefab,
        GameObject enemyPrefab, GameObject bossPrefab)
    {
        // El jugador de la escena era del montaje single-player: en red los
        // jugadores los spawnea el host desde el PlayerPrefab.
        GameObject legacyPlayer = GameObject.Find("Player");
        if (legacyPlayer != null) Object.DestroyImmediate(legacyPlayer);

        DungeonSystem.DungeonManager dungeonManager = Object.FindFirstObjectByType<DungeonSystem.DungeonManager>();
        DungeonSystem.RoomCameraFollow cameraFollow = Object.FindFirstObjectByType<DungeonSystem.RoomCameraFollow>();

        if (dungeonManager == null) Debug.LogError("[ProtocolSetup] No se encontro DungeonManager en la escena.");
        if (cameraFollow == null) Debug.LogWarning("[ProtocolSetup] No se encontro CameraFollow: la camara no seguira al jugador.");

        // El piso lo decide el host al empezar la run, nunca el Start de la escena.
        if (dungeonManager != null)
        {
            var dungeonSo = new SerializedObject(dungeonManager);
            SetBool(dungeonSo, "generateOnStart", false);
            dungeonSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(dungeonManager);
        }

        // Re-ejecutar el setup no debe duplicar el objeto de sistemas: dos
        // RunBootstrapper se suscribirian dos veces a la fase y generarian los
        // enemigos por duplicado, y dos GameUI construirian dos canvas.
        GameObject[] allObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < allObjects.Length; i++)
        {
            if (allObjects[i] != null && allObjects[i].name == "Protocol Systems")
                Object.DestroyImmediate(allObjects[i]);
        }

        var systems = new GameObject("Protocol Systems");

        var launcher = systems.AddComponent<NetworkLauncher>();
        var launcherSo = new SerializedObject(launcher);
        SetRef(launcherSo, "networkManager", networkManagerObject.GetComponent<NetworkManager>());
        SetRef(launcherSo, "transport", networkManagerObject.GetComponent<UnityTransport>());
        SetRef(launcherSo, "gameFlowPrefab", gameFlowPrefab);
        SetUShort(launcherSo, "port", Port);
        launcherSo.ApplyModifiedPropertiesWithoutUndo();

        var bootstrapper = systems.AddComponent<RunBootstrapper>();
        var bootstrapperSo = new SerializedObject(bootstrapper);
        SetRef(bootstrapperSo, "dungeonManager", dungeonManager);
        SetRef(bootstrapperSo, "cameraFollow", cameraFollow);
        SetRef(bootstrapperSo, "enemyPrefab", enemyPrefab);
        SetRef(bootstrapperSo, "bossPrefab", bossPrefab);
        SetFloat(bootstrapperSo, "playerSpawnHeight", 0.1f);
        bootstrapperSo.ApplyModifiedPropertiesWithoutUndo();

        systems.AddComponent<GameUI>();

        EditorUtility.SetDirty(systems);

        // El evento de la escena necesita un modulo de UI compatible con el
        // Input System activo; GameUI lo crea en runtime, no aqui.
        if (Object.FindFirstObjectByType<GameUI>() == null)
            Debug.LogWarning("[ProtocolSetup] GameUI no quedo en la escena.");

        if (Object.FindFirstObjectByType<MinimapController>() == null)
            Debug.Log("[ProtocolSetup] No hay MinimapController en la escena; el minimapa quedara inactivo.");
    }

    private static void EnsureBuildSettings()
    {
        string scenePath = SceneManager.GetActiveScene().path;
        if (string.IsNullOrEmpty(scenePath)) return;

        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (EditorBuildSettingsScene scene in scenes)
        {
            if (scene.path == scenePath) return;
        }

        scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"[ProtocolSetup] '{scenePath}' agregada a las escenas del build.");
    }

    // ---------------- Utilidades de SerializedObject ----------------

    private static void SetRef(SerializedObject so, string propertyName, Object value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property == null)
        {
            Debug.LogWarning($"[ProtocolSetup] No existe la propiedad '{propertyName}' en {so.targetObject.GetType().Name}.");
            return;
        }
        property.objectReferenceValue = value;
    }

    private static void SetInt(SerializedObject so, string propertyName, int value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property != null) property.intValue = value;
    }

    private static void SetFloat(SerializedObject so, string propertyName, float value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property != null) property.floatValue = value;
    }

    private static void SetBool(SerializedObject so, string propertyName, bool value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property != null) property.boolValue = value;
    }

    private static void SetUShort(SerializedObject so, string propertyName, ushort value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property != null) property.intValue = value;
    }
}
#endif
