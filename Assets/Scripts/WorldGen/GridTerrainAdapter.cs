using UnityEngine;
using VContainer;

/// 
/// GridTerrainAdapter — "мост" между новой Grid-системой и старым кодом
/// Позволяет PropSpawner и другим системам работать без переписывания
/// 
public class GridTerrainAdapter : MonoBehaviour
{
    private static GridTerrainAdapter _instance;
    public static GridTerrainAdapter Instance => _instance;

    [SerializeField] private GridWorldGenerator _gridGen;
    private MeshCollider _meshCollider;

    [SerializeField] private LayerMask groundLayer;

    private void Awake()
    {
        if (_instance == null)
            _instance = this;
        else
        {
            Destroy(this);
            return;
        }
    }

    private void Start()
    {
        // Fallback если не назначен в инспекторе
        if (_gridGen == null)
        {
            _gridGen = GetComponent<GridWorldGenerator>();
        }

        _meshCollider = GetComponent<MeshCollider>();

        if (_gridGen == null)
            Debug.LogError("[GridTerrainAdapter] ❌ GridWorldGenerator не найден!");
    }

    /// <summary>Эмуляция Terrain.SampleHeight — возвращает высоту поверхности</summary>
    public float SampleHeight(Vector3 worldPos)
    {
        if (_gridGen == null) return 0f;
        return _gridGen.GetSurfaceHeight(worldPos);
    }

    /// <summary>Эмуляция TerrainData.GetSteepness — возвращает угол наклона</summary>
    public float GetSteepness(Vector3 worldPos)
    {
        if (_gridGen == null) return 0f;
        return _gridGen.GetSteepness(worldPos);
    }

    /// <summary>Эмуляция с нормализованными координатами [0..1]</summary>
    public float GetSteepness(float normX, float normZ)
    {
        if (_gridGen == null) return 0f;

        Vector3 worldSize = _gridGen.GetWorldSize();
        Vector3 worldCenter = _gridGen.GetWorldCenter();

        Vector3 worldPos = new Vector3(
            worldCenter.x + (normX - 0.5f) * worldSize.x,
            0f,
            worldCenter.z + (normZ - 0.5f) * worldSize.z
        );

        return _gridGen.GetSteepness(worldPos);
    }

    /// <summary>Эмуляция Terrain.terrainData.size</summary>
    public Vector3 GetSize()
    {
        if (_gridGen == null) return Vector3.zero;
        return _gridGen.GetWorldSize();
    }

    /// <summary>Эмуляция Terrain.transform.position</summary>
    public Vector3 GetPosition() => transform.position;

    /// <summary>Проверка: можно ли ходить по этой позиции?</summary>
    public bool IsValidPosition(Vector3 worldPos)
    {
        return _gridGen != null && _gridGen.IsWalkable(worldPos);
    }

    /// <summary>Прямой доступ к GridWorldGenerator</summary>
    public GridWorldGenerator GetGridGenerator() => _gridGen;

    /// <summary>Готова ли система?</summary>
    public bool IsReady => _gridGen != null && _gridGen.IsGenerated;

    /// <summary>Корутина для ожидания готовности</summary>
    public System.Collections.IEnumerator WaitForReady()
    {
        float timeout = 5f;
        float elapsed = 0f;

        while (!IsReady && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!IsReady)
            Debug.LogWarning("[GridTerrainAdapter] ⚠️ Таймаут ожидания готовности!");
    }
}