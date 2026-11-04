using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Pool;

public class ObjectPoolManager : MonoBehaviour
{
    [SerializeField] private bool _addToDontDestroyOnLoad = false;

    [Header("Prewarm")]
    [Tooltip("Pools filled on Start so combat reuses bolts instead of allocating mid-fight. " +
             "Size each count to peak concurrent live projectiles.")]
    [SerializeField] private List<PrewarmEntry> _prewarm = new List<PrewarmEntry>();

    private GameObject _emptyHolder;

    private static GameObject _gameObjectsEmpty;
    private static GameObject _plasmaProjectilesEmpty;
    private static GameObject _missilesEmpty;
    private static GameObject _flaresEmpty;

    private static Dictionary<GameObject, ObjectPool<GameObject>> _objectPools;
    private static Dictionary<GameObject, GameObject> _cloneToPrefabMap;

    public enum PoolType
    {
        GameObjects,
        Plasma,
        Missile,
        Flare
    }

    public static PoolType PoolingType;

    [System.Serializable]
    private struct PrewarmEntry
    {
        public GameObject prefab;
        public int count;
        public PoolType poolType;
    }

    private void Awake()
    {
        _objectPools = new Dictionary<GameObject, ObjectPool<GameObject>>();
        _cloneToPrefabMap = new Dictionary<GameObject, GameObject>();

        SetupEmpties();
    }

    private void Start()
    {
        // Runs after every Awake, so the static pools are initialized and prefabs are ready.
        for (int i = 0; i < _prewarm.Count; i++)
        {
            PrewarmEntry e = _prewarm[i];
            if (e.prefab != null && e.count > 0)
                PrewarmPool(e.prefab, e.count, e.poolType);
        }
    }

    private void SetupEmpties()
    {
        _emptyHolder = new GameObject("Object Pools");

        _gameObjectsEmpty = new GameObject("GameObjects");
        _gameObjectsEmpty.transform.SetParent(_emptyHolder.transform);

        _plasmaProjectilesEmpty = new GameObject("Plasma Projectiles");
        _plasmaProjectilesEmpty.transform.SetParent(_emptyHolder.transform);

        _missilesEmpty = new GameObject("Missiles");
        _missilesEmpty.transform.SetParent(_emptyHolder.transform);

        _flaresEmpty = new GameObject("Flares");
        _flaresEmpty.transform.SetParent(_emptyHolder.transform);

        if (_addToDontDestroyOnLoad)
        {
            DontDestroyOnLoad(_plasmaProjectilesEmpty.transform.root);
        }
    }

    private static void CreatePool(GameObject prefab, Vector2 pos, Quaternion rot, PoolType poolType = PoolType.GameObjects)
    {
        ObjectPool<GameObject> pool = new ObjectPool<GameObject>(
            createFunc: () => CreateObject(prefab, pos, rot, poolType),
            actionOnGet: OnGetObject,
            actionOnRelease: OnReleaseObject,
            actionOnDestroy: OnDestroyObject
            );

        _objectPools.Add(prefab, pool);
    }

    private static GameObject CreateObject(GameObject prefab, Vector2 pos, Quaternion rot, PoolType poolType = PoolType.GameObjects)
    {
        prefab.SetActive(false);

        GameObject obj = Instantiate(prefab, pos, rot);

        prefab.SetActive(true);

        GameObject parentObject = SetParentObject(poolType);
        obj.transform.SetParent(parentObject.transform);

        return obj;
    }

    private static void OnGetObject(GameObject obj)
    {
        // optional logic for getting obj
    }

    private static void OnReleaseObject(GameObject obj)
    {
        obj.SetActive(false);
    }

    private static void OnDestroyObject(GameObject obj) 
    {
        if (_cloneToPrefabMap.ContainsKey(obj))
        {
            _cloneToPrefabMap.Remove(obj);
        }
    }

    private static GameObject SetParentObject(PoolType poolType)
    {
        switch (poolType)
        {
            case PoolType.GameObjects:

                return _gameObjectsEmpty;
            case PoolType.Plasma:

                return _plasmaProjectilesEmpty;
            case PoolType.Missile:

                return _missilesEmpty;
            case PoolType.Flare:

                return _flaresEmpty;
            default:
                return null;
        }
    }

    private static T SpawnObject<T>(GameObject objToSpawn, Vector2 spawnPos, Quaternion spawnRot, PoolType poolType = PoolType.GameObjects) where T : Object
    {
        if (!_objectPools.ContainsKey(objToSpawn))
        {
            CreatePool(objToSpawn, spawnPos, spawnRot, poolType);
        }

        GameObject obj = _objectPools[objToSpawn].Get();

        if (obj != null)
        {
            if (!_cloneToPrefabMap.ContainsKey(obj))
            {
                _cloneToPrefabMap.Add(obj, objToSpawn);
            }

            obj.transform.position = spawnPos;
            obj.transform.rotation = spawnRot;
            obj.SetActive(true);

            if (typeof(T) == typeof(GameObject))
            {
                return obj as T;
            }

            T component = obj.GetComponent<T>();
            if (component == null)
            {
                Debug.LogError($"Object {objToSpawn.name} doesn't have component of type {typeof(T)}");
                return null;
            }

            return component;
        }

        return null;
    }

    public static T SpawnObject<T>(T typePrefab, Vector2 spawnPos, Quaternion spawnRot, PoolType poolType = PoolType.GameObjects) where T : Component
    {
        return SpawnObject<T>(typePrefab.gameObject, spawnPos, spawnRot, poolType);
    }

    public static GameObject SpawnObject(GameObject objToSpawn, Vector2 spawnPos, Quaternion spawnRot, PoolType poolTYpe = PoolType.GameObjects)
    {
        return SpawnObject<GameObject>(objToSpawn, spawnPos, spawnRot, poolTYpe);
    }

    public static void PrewarmPool(GameObject prefab, int count, PoolType poolType = PoolType.GameObjects)
    {
        if (prefab == null || count <= 0) return;

        // Get all N first (forcing N distinct creations), THEN release them. Releasing inside the
        // same loop would just hand the same object back on the next Get and leave the pool at 1.
        List<GameObject> temp = new List<GameObject>(count);
        for (int i = 0; i < count; i++)
            temp.Add(SpawnObject(prefab, Vector2.zero, Quaternion.identity, poolType));
        for (int i = 0; i < temp.Count; i++)
            ReturnObjectToPool(temp[i], poolType);
    }

    public static void ReturnObjectToPool(GameObject obj, PoolType poolType = PoolType.GameObjects)
    {
        if (_cloneToPrefabMap.TryGetValue(obj, out GameObject prefab))
        {
            GameObject parentObject = SetParentObject(poolType);

            if (obj.transform.parent != parentObject.transform)
            {
                obj.transform.SetParent(parentObject.transform);
            }

            if (_objectPools.TryGetValue(prefab, out ObjectPool<GameObject> pool))
            {
                pool.Release(obj);
            }
        }
        else
        {
            Debug.LogWarning("Trying to return an object that is not pooled: " + obj.name);
        }
    }
}
