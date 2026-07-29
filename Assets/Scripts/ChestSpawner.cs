using UnityEngine;
using System.Collections.Generic;
using VContainer;

/// <summary>
/// ChestSpawner v9 — АДАПТИРОВАН ДЛЯ GRID СИСТЕМЫ С DI
///
/// ИЗМЕНЕНИЯ v9:
///   ✦ Поддержка GridTerrainAdapter через DI инжекцию
///   ✦ Fallback на поиск в сцене если DI не сработал
///   ✦ Упрощенная логика определения высоты
/// </summary>
[RequireComponent(typeof(Transform))]
public class ChestSpawner : MonoBehaviour
{
    [Header("Ссылки")]
    [SerializeField] private GameObject chestPrefab;
    [SerializeField] private ItemDatabase itemDatabase;

    [Header("Настройки спавна")]
    [SerializeField] private int chestCount = 5;
    [SerializeField] private float minimumDistanceBetweenChests = 5f;
    [SerializeField] private float minimumDistanceFromCenter = 3f;

    [Header("Фильтрация позиций")]
    [Tooltip("Максимальный наклон поверхности в градусах. " +
             "8–12° = только плоские плато и пологие склоны.")]
    [SerializeField] private float maxSteepnessDegrees = 10f;

    [Tooltip("Отступ от края карты (нормализованный, 0..0.5).")]
    [SerializeField][Range(0.05f, 0.35f)] private float edgeSafetyMargin = 0.20f;

    [Header("Настройки карты")]
    [Tooltip("Смещение сундука над поверхностью земли.")]
    [SerializeField] private float spawnHeight = 0.4f;

    [Tooltip("Слой terrain для raycast.")]
    [SerializeField] private LayerMask groundLayer;

    [SerializeField] private int maxAttempts = 60;

    [Header("Инициализация")]
    [Tooltip("Максимальное время ожидания генерации мира (сек)")]
    [SerializeField] private float maxTerrainWaitTime = 5f;

    [Tooltip("Интервал проверки готовности (сек)")]
    [SerializeField] private float terrainCheckInterval = 0.1f;

    private List<Chest> _spawnedChests = new List<Chest>();
    private System.Random _seededRandom;
    private int _chestOpenCount = 0;
    private bool _hasSpawned = false;

    // ─── ЗАВИСИМОСТИ ──────────────────────────────────────────────────────────
    [Inject] private GridTerrainAdapter _gridAdapter;
    [Inject] private WorldSeedProvider _seedProvider;

    [Inject]
    public void Construct(GameStateService gameState, WorldSeedProvider seedProvider)
    {
        _seedProvider = seedProvider;
        _seededRandom = seedProvider?.GetRandom("chests");
    }

    private void Start()
    {
        if (itemDatabase == null)
            itemDatabase = Resources.Load<ItemDatabase>("ItemDatabase");

        if (itemDatabase == null)
            Debug.LogError("[ChestSpawner] ❌ ItemDatabase не найдена в Resources!");

        // Установка groundLayer если не назначен
        if (groundLayer == 0)
        {
            int groundLayerIndex = LayerMask.NameToLayer("Ground");
            if (groundLayerIndex >= 0)
            {
                groundLayer = 1 << groundLayerIndex;
                Debug.Log($"[ChestSpawner] groundLayer автоматически установлен на слой 'Ground'");
            }
            else
            {
                groundLayer = LayerMask.GetMask("Default", "Ground");
                Debug.LogWarning("[ChestSpawner] Слой 'Ground' не найден! Используем Default.");
            }
        }

        // Fallback если инжекция не сработала
        if (_seedProvider == null)
        {
            Debug.LogWarning("[ChestSpawner] ⚠️ WorldSeedProvider не инжектирован — создаю локальный.");
            var fallback = new WorldSeedProvider();
            fallback.Initialize();
            _seedProvider = fallback;
            _seededRandom = fallback.GetRandom("chests");
        }

        if (_seededRandom == null && _seedProvider != null)
        {
            _seededRandom = _seedProvider.GetRandom("chests");
        }

        // Ждём генерации мира перед спавном
        StartCoroutine(WaitForWorldAndSpawn());
    }

    /// <summary>
    /// Корутина ожидания генерации мира
    /// </summary>
    private System.Collections.IEnumerator WaitForWorldAndSpawn()
    {
        float waitTime = 0f;

        // Ждём пока появится GridTerrainAdapter (через DI или поиск)
        while (_gridAdapter == null && waitTime < maxTerrainWaitTime)
        {
            _gridAdapter = FindFirstObjectByType<GridTerrainAdapter>();
            
            if (_gridAdapter == null)
            {
                yield return new WaitForSeconds(terrainCheckInterval);
                waitTime += terrainCheckInterval;
            }
        }

        // Если Grid нет — пробуем Terrain
        if (_gridAdapter == null)
        {
            var terrain = Terrain.activeTerrain;
            if (terrain == null)
            {
                Debug.LogError("[ChestSpawner] ❌ Ни GridTerrainAdapter, ни Terrain не найдены! Сундуки не будут заспавнены.");
                yield break;
            }
            Debug.Log("[ChestSpawner] ⚠️ Используется старая Terrain система");
        }
        else
        {
            // Ждём пока Grid сгенерируется
            yield return StartCoroutine(_gridAdapter.WaitForReady());
        }

        // Ждём ещё один кадр чтобы всё полностью инициализировалось
        yield return null;

        SpawnChests();
    }

    private void SpawnChests()
    {
        if (_hasSpawned) return;
        _hasSpawned = true;

        if (chestPrefab == null)
        {
            Debug.LogError("[ChestSpawner] ❌ chestPrefab не назначен!");
            return;
        }

        _spawnedChests.Clear();
        List<Vector3> spawnedPositions = new List<Vector3>();

        for (int i = 0; i < chestCount; i++)
        {
            Vector3 spawnPos = FindValidSpawnPosition(spawnedPositions);

            if (spawnPos != Vector3.zero && IsValidPosition(spawnPos))
            {
                SpawnChestAtPosition(spawnPos);
                spawnedPositions.Add(spawnPos);
            }
            else
            {
                Debug.LogWarning($"[ChestSpawner] Не удалось найти позицию для сундука #{i + 1}");
            }
        }

        Debug.Log($"[ChestSpawner] ✅ Спавнено {_spawnedChests.Count} сундуков из {chestCount}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ВАЛИДНОЙ ПОЗИЦИИ
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 FindValidSpawnPosition(List<Vector3> existingPositions)
    {
        // Определяем какую систему использовать
        bool useGrid = _gridAdapter != null && _gridAdapter.IsReady;
        
        // Получаем размеры карты
        Vector3 mapSize;
        Vector3 mapCenter;
        
        if (useGrid)
        {
            mapSize = _gridAdapter.GetSize();
            mapCenter = _gridAdapter.GetPosition();
        }
        else
        {
            var terrain = Terrain.activeTerrain;
            if (terrain == null) return Vector3.zero;
            mapSize = terrain.terrainData.size;
            mapCenter = terrain.transform.position + mapSize * 0.5f;
        }

        // Безопасный диапазон нормализованных координат [margin .. 1-margin]
        float lo = edgeSafetyMargin;
        float hi = 1f - edgeSafetyMargin;
        float range = hi - lo;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Генерируем ВНУТРИ безопасной зоны
            float normX = lo + (float)_seededRandom.NextDouble() * range;
            float normZ = lo + (float)_seededRandom.NextDouble() * range;

            float worldX = mapCenter.x + (normX - 0.5f) * mapSize.x;
            float worldZ = mapCenter.z + (normZ - 0.5f) * mapSize.z;
            Vector3 candidate = new Vector3(worldX, 0f, worldZ);

            // Проверка расстояния от центра
            Vector3 center2D = new Vector3(transform.position.x, 0f, transform.position.z);
            if (Vector3.Distance(new Vector3(candidate.x, 0f, candidate.z), center2D)
                < minimumDistanceFromCenter) continue;

            // Проверка расстояния от других сундуков
            bool tooClose = false;
            foreach (var pos in existingPositions)
            {
                if (Vector3.Distance(new Vector3(candidate.x, 0f, candidate.z),
                                     new Vector3(pos.x, 0f, pos.z)) < minimumDistanceBetweenChests)
                { tooClose = true; break; }
            }
            if (tooClose) continue;

            // Проверка крутизны — только плоские плато
            float steepness;
            if (useGrid)
                steepness = _gridAdapter.GetSteepness(candidate);
            else
            {
                var terrain = Terrain.activeTerrain;
                steepness = terrain.terrainData.GetSteepness(normX, normZ);
            }
                
            if (steepness > maxSteepnessDegrees) continue;

            // Точная высота поверхности
            Vector3 groundPos = FindGroundPosition(candidate);
            if (groundPos != Vector3.zero && IsValidPosition(groundPos))
                return groundPos;
        }

        return Vector3.zero;
    }

    private Vector3 FindGroundPosition(Vector3 pos)
    {
        bool useGrid = _gridAdapter != null && _gridAdapter.IsReady;
        
        if (useGrid)
        {
            // Grid система
            float height = _gridAdapter.SampleHeight(pos);
            if (height > -100f)
            {
                return new Vector3(pos.x, height + spawnHeight, pos.z);
            }
        }
        else
        {
            // Terrain система (fallback)
            var terrain = Terrain.activeTerrain;
            if (terrain != null)
            {
                float sampleY = terrain.SampleHeight(pos);
                if (sampleY >= 0f)
                {
                    return new Vector3(pos.x, sampleY + terrain.transform.position.y + spawnHeight, pos.z);
                }
            }
        }

        // Fallback: raycast сверху вниз
        if (Physics.Raycast(pos + Vector3.up * 100f, Vector3.down, out RaycastHit hit, 200f, groundLayer))
        {
            return new Vector3(pos.x, hit.point.y + spawnHeight, pos.z);
        }

        return Vector3.zero;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВАЛИДАЦИЯ ПОЗИЦИИ
    // ─────────────────────────────────────────────────────────────────────────

    private bool IsValidPosition(Vector3 pos)
    {
        if (pos == Vector3.zero) return false;
        if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) return false;
        if (float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z)) return false;

        // Проверка для Grid системы
        if (_gridAdapter != null)
        {
            return _gridAdapter.IsValidPosition(pos);
        }

        // Проверка для Terrain системы
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null)
        {
            float terrainBottomY = terrain.transform.position.y;
            float terrainTopY = terrain.transform.position.y + terrain.terrainData.size.y;

            if (pos.y < terrainBottomY - 1f) return false;
            if (pos.y > terrainTopY + 10f) return false;
        }

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СПАВН СУНДУКА
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnChestAtPosition(Vector3 pos)
    {
        var chestObj = Instantiate(chestPrefab, pos, Quaternion.identity, transform);
        var chest = chestObj.GetComponent<Chest>();

        if (chest != null)
        {
            chest.Init(() => ++_chestOpenCount);
            _spawnedChests.Add(chest);
            Debug.Log($"[ChestSpawner] ✅ Сундук заспавнен на позиции {pos}");
        }
        else
        {
            Debug.LogError("[ChestSpawner] ❌ chestPrefab не имеет компонента Chest!");
            Destroy(chestObj);
        }
    }

    public List<Chest> GetSpawnedChests() => new List<Chest>(_spawnedChests);
    public int GetRemainingChestCount() => _spawnedChests.Count;

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, 30f);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, minimumDistanceFromCenter);
    }
}
