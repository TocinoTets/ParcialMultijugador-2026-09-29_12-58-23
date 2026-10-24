using System.Collections.Generic;
using DungeonSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Construye el sistema de mazmorra nuevo: el prefab de sala base, la base
/// visual y el cableado de la escena.
///
/// Es idempotente: volver a ejecutarlo deja el proyecto igual, no duplica nada.
///
/// Geometría del prefab (clave para que el jugador no aparezca bajo el suelo):
///  - La celda mide 20x20 y el GRID avanza 20 unidades por celda.
///  - El suelo es una losa de 20x20 con su cara SUPERIOR exactamente en y = 0.
///    Por eso el spawn de una criatura es FloorSurfaceY + mitad de su altura.
///  - Las paredes van de 9.5 a 10.0 y las del vecino de 10.0 a 10.5: cada sala
///    tiene su propia losa en la frontera, sin solaparse ni dejar huecos.
///  - Cada lado lleva 2 tramos de pared, una losa ("gate") que tapa el hueco
///    cuando NO hay vecino, y una puerta que solo existe cuando sí lo hay.
/// </summary>
public static class DungeonSetup
{
    private const float RoomSize = 20f;
    private const float Half = RoomSize * 0.5f;          // 10
    private const float WallThickness = 0.5f;
    private const float WallHeight = 4f;
    private const float DoorWidth = 4f;
    private const float FloorThickness = 0.4f;
    private const float WallCenter = Half - WallThickness * 0.5f;   // 9.75

    private const string PrefabFolder = "Assets/Prefabs/Dungeon";
    private const string PrefabPath = PrefabFolder + "/DungeonRoom.prefab";
    private const string DatabaseFolder = "Assets/Dungeon";
    private const string DatabasePath = DatabaseFolder + "/RoomVisualDatabase.asset";
    private const string MaterialsFolder = "Assets/Materials";

    [MenuItem("Protocol/Configurar sistema de mazmorra")]
    public static void Configure()
    {
        EnsureFolder("Assets/Prefabs");
        EnsureFolder(PrefabFolder);
        EnsureFolder(DatabaseFolder);
        EnsureFolder(MaterialsFolder);

        Material floorMaterial = LoadOrCreateMaterial("Dungeon_Floor", new Color(0.12f, 0.13f, 0.17f));
        Material wallMaterial = LoadOrCreateMaterial("Dungeon_Wall", new Color(0.22f, 0.24f, 0.33f));
        Material doorOpenMaterial = LoadOrCreateMaterial("Dungeon_DoorOpen", new Color(0.10f, 0.80f, 0.95f), emissive: true);
        Material doorClosedMaterial = LoadOrCreateMaterial("Dungeon_DoorClosed", new Color(0.95f, 0.28f, 0.22f), emissive: true);

        GameObject roomPrefab = BuildRoomPrefab(floorMaterial, wallMaterial, doorOpenMaterial, doorClosedMaterial);
        RoomVisualDatabase database = CreateDatabase(roomPrefab, floorMaterial);
        WireScene(roomPrefab, database);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[DungeonSetup] Prefab de sala: {PrefabPath}");
        Debug.Log($"[DungeonSetup] Base visual: {DatabasePath}");
        Debug.Log("[DungeonSetup] Escena cableada. Ejecutar tambien Protocol/Configure de ProtocolSetup para el resto de la escena.");
    }

    // ---------------- Prefab de sala ----------------

    private static GameObject BuildRoomPrefab(Material floorMaterial, Material wallMaterial,
        Material doorOpenMaterial, Material doorClosedMaterial)
    {
        var root = new GameObject("DungeonRoom");

        // Trigger de ocupación: el RoomController vive en este mismo objeto para
        // recibir OnTriggerEnter/Exit. Se extiende solo por el interior de la
        // sala (19x19) para no solaparse con la celda vecina.
        var trigger = root.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, WallHeight * 0.5f, 0f);
        trigger.size = new Vector3(RoomSize - 1f, WallHeight, RoomSize - 1f);

        var roomController = root.AddComponent<RoomController>();

        GameObject floor = CreateBox("Floor",
            new Vector3(0f, -FloorThickness * 0.5f, 0f),
            new Vector3(RoomSize, FloorThickness, RoomSize),
            floorMaterial, root.transform);

        var sides = new List<RoomSideBinding>();

        foreach (RoomDirection direction in DungeonGrid.AllDirections)
        {
            sides.Add(BuildSide(root.transform, direction, wallMaterial, doorOpenMaterial, doorClosedMaterial));
        }

        var obstacleSpawner = root.AddComponent<RoomObstacleSpawner>();
        for (int i = 0; i < 4; i++)
        {
            var point = new GameObject($"Obstacle_{i}");
            point.transform.SetParent(root.transform, false);

            float angle = i * Mathf.PI * 0.5f;
            point.transform.localPosition = new Vector3(Mathf.Cos(angle) * 5f, 0f, Mathf.Sin(angle) * 5f);
            point.AddComponent<RoomObstacleSpawnPoint>();
        }

        // Cableado del RoomController (campos privados serializados).
        var so = new SerializedObject(roomController);

        SerializedProperty floorProperty = so.FindProperty("floorRenderer");
        if (floorProperty != null) floorProperty.objectReferenceValue = floor.GetComponent<Renderer>();

        SerializedProperty sidesProperty = so.FindProperty("sides");
        if (sidesProperty != null)
        {
            sidesProperty.arraySize = sides.Count;

            for (int i = 0; i < sides.Count; i++)
            {
                SerializedProperty element = sidesProperty.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("direction").enumValueIndex = (int)sides[i].direction;
                element.FindPropertyRelative("gate").objectReferenceValue = sides[i].gate;
                element.FindPropertyRelative("door").objectReferenceValue = sides[i].door;
            }
        }
        else
        {
            Debug.LogWarning("[DungeonSetup] No se encontro la propiedad 'sides' en RoomController.");
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);

        // La referencia al spawner solo evita el warning de variable sin usar.
        if (obstacleSpawner == null) Debug.LogWarning("[DungeonSetup] RoomObstacleSpawner no quedo en el prefab.");

        return prefab;
    }

    private static RoomSideBinding BuildSide(Transform parent, RoomDirection direction,
        Material wallMaterial, Material doorOpenMaterial, Material doorClosedMaterial)
    {
        bool alongZ = direction == RoomDirection.Up || direction == RoomDirection.Down;   // lados en Z
        bool positive = direction == RoomDirection.Up || direction == RoomDirection.Right;

        float sign = positive ? 1f : -1f;
        float boundary = sign * WallCenter;

        var sideRoot = new GameObject($"Side_{direction}");
        sideRoot.transform.SetParent(parent, false);

        // Los dos tramos de pared que flanquean el hueco central.
        float segmentLength = (RoomSize - DoorWidth) * 0.5f;    // 8
        float segmentOffset = (DoorWidth + segmentLength) * 0.5f; // 6

        for (int i = -1; i <= 1; i += 2)
        {
            Vector3 position;
            Vector3 scale;

            if (alongZ)
            {
                position = new Vector3(i * segmentOffset, WallHeight * 0.5f, boundary);
                scale = new Vector3(segmentLength, WallHeight, WallThickness);
            }
            else
            {
                position = new Vector3(boundary, WallHeight * 0.5f, i * segmentOffset);
                scale = new Vector3(WallThickness, WallHeight, segmentLength);
            }

            CreateBox($"Wall_{direction}_{(i < 0 ? "A" : "B")}", position, scale, wallMaterial, sideRoot.transform);
        }

        // Losa que tapa el hueco central. Activa por defecto: si el builder no
        // encuentra vecino, la sala ya está cerrada.
        Vector3 gatePosition = alongZ
            ? new Vector3(0f, WallHeight * 0.5f, boundary)
            : new Vector3(boundary, WallHeight * 0.5f, 0f);
        Vector3 gateScale = alongZ
            ? new Vector3(DoorWidth, WallHeight, WallThickness)
            : new Vector3(WallThickness, WallHeight, DoorWidth);

        GameObject gate = CreateBox($"Gate_{direction}", gatePosition, gateScale, wallMaterial, sideRoot.transform);

        // Puerta: mismo hueco, otro material. Empieza inactiva; el RoomController
        // la activa solo donde hay conexión.
        GameObject doorObject = CreateBox($"Door_{direction}", gatePosition, gateScale, doorOpenMaterial, sideRoot.transform);

        var door = doorObject.AddComponent<DoorController>();
        var doorSo = new SerializedObject(door);
        doorSo.FindProperty("visual").objectReferenceValue = doorObject.GetComponent<Renderer>();
        doorSo.FindProperty("blockingCollider").objectReferenceValue = doorObject.GetComponent<Collider>();
        doorSo.FindProperty("openDoorMaterial").objectReferenceValue = doorOpenMaterial;
        doorSo.FindProperty("closedDoorMaterial").objectReferenceValue = doorClosedMaterial;
        doorSo.ApplyModifiedPropertiesWithoutUndo();

        doorObject.SetActive(false);

        return new RoomSideBinding
        {
            direction = direction,
            gate = gate,
            door = door
        };
    }

    private static GameObject CreateBox(string name, Vector3 localPosition, Vector3 scale,
        Material material, Transform parent)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = scale;

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null && material != null) renderer.sharedMaterial = material;

        return go;
    }

    // ---------------- Base visual ----------------

    private static RoomVisualDatabase CreateDatabase(GameObject roomPrefab, Material floorMaterial)
    {
        var database = AssetDatabase.LoadAssetAtPath<RoomVisualDatabase>(DatabasePath);

        if (database == null)
        {
            database = ScriptableObject.CreateInstance<RoomVisualDatabase>();
            AssetDatabase.CreateAsset(database, DatabasePath);
        }

        // Un prefab por cada una de las 16 máscaras: así ninguna combinación de
        // conexiones puede quedarse sin sala, que era el fallo del sistema viejo.
        database.ConfigureFromPrefab(roomPrefab, floorMaterial);

        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();

        return database;
    }

    // ---------------- Escena ----------------

    private static void WireScene(GameObject roomPrefab, RoomVisualDatabase database)
    {
        // Cualquier componente antiguo (o script ya borrado) de estos objetos se
        // retira para no dejar dos sistemas compitiendo por la misma escena.
        var managerObject = FindOrCreate("DungeonManager", out bool managerCreated);
        var builderObject = FindOrCreate("DungeonBuilder", out bool builderCreated);
        var minimapObject = FindOrCreate("Minimap", out bool minimapCreated);

        if (managerCreated || builderCreated || minimapCreated)
            Debug.Log("[DungeonSetup] Se crearon los objetos que faltaban de la mazmorra.");

        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(managerObject);
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(builderObject);
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(minimapObject);

        Transform dungeonRoot = FindOrCreate("Dungeon", out _).transform;

        var builder = builderObject.GetComponent<DungeonBuilder>();
        if (builder == null) builder = builderObject.AddComponent<DungeonBuilder>();

        var builderSo = new SerializedObject(builder);
        builderSo.FindProperty("database").objectReferenceValue = database;
        builderSo.FindProperty("roomSize").floatValue = RoomSize;
        builderSo.FindProperty("dungeonRoot").objectReferenceValue = dungeonRoot;
        builderSo.ApplyModifiedPropertiesWithoutUndo();

        var minimap = minimapObject.GetComponent<MinimapController>();
        if (minimap == null) minimap = minimapObject.AddComponent<MinimapController>();

        var camera = Camera.main;
        RoomCameraFollow cameraFollow = null;

        if (camera != null)
        {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(camera.gameObject);

            cameraFollow = camera.GetComponent<RoomCameraFollow>();
            if (cameraFollow == null) cameraFollow = camera.gameObject.AddComponent<RoomCameraFollow>();

            var cameraSo = new SerializedObject(cameraFollow);
            cameraSo.FindProperty("roomSize").floatValue = RoomSize;
            cameraSo.FindProperty("player").objectReferenceValue = null;
            cameraSo.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            Debug.LogWarning("[DungeonSetup] No se encontro la camara principal: la camara no seguira al jugador.");
        }

        var manager = managerObject.GetComponent<DungeonManager>();
        if (manager == null) manager = managerObject.AddComponent<DungeonManager>();

        var managerSo = new SerializedObject(manager);
        managerSo.FindProperty("builder").objectReferenceValue = builder;
        managerSo.FindProperty("minimap").objectReferenceValue = minimap;
        managerSo.FindProperty("cameraFollow").objectReferenceValue = cameraFollow;
        managerSo.FindProperty("generateOnStart").boolValue = false;

        // Una sala de jefe es obligatoria para poder cerrar la run.
        SerializedProperty specials = managerSo.FindProperty("specialRoomCount");
        if (specials != null && specials.intValue < 1) specials.intValue = 2;

        managerSo.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(builderObject);
        EditorUtility.SetDirty(minimapObject);
        EditorUtility.SetDirty(managerObject);
        if (camera != null) EditorUtility.SetDirty(camera.gameObject);

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        if (roomPrefab == null) Debug.LogWarning("[DungeonSetup] El prefab de sala no se genero.");
    }

    private static GameObject FindOrCreate(string name, out bool created)
    {
        GameObject existing = GameObject.Find(name);
        created = existing == null;

        return created ? new GameObject(name) : existing;
    }

    // ---------------- Utilidades ----------------

    private static Material LoadOrCreateMaterial(string name, Color color, bool emissive = false)
    {
        string path = $"{MaterialsFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;

        if (emissive)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 1.5f);
        }

        EditorUtility.SetDirty(material);

        return material;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;

        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);

        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);

        AssetDatabase.CreateFolder(parent, leaf);
    }
}
