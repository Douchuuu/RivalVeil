using UnityEngine;
using System.Collections.Generic;
using VContainer;

/// <summary>
/// PropSpawner v2.0 — АДАПТИРОВАН ДЛЯ GridWorldGenerator
/// 
/// ИЗМЕНЕНИЯ:
///   ✅ Удалена зависимость от старого RampSpawner.
///   ✅ Интеграция с GridTerrainAdapter (высота, крутизна, границы).
///   ✅ Замена проверки "на рампе" на проверку крутизны (т.к. в Grid рампа = наклон).
///   ✅ Fallback на старый Terrain, если Grid система не найдена (для отладки).
/// </summary>
public class PropSpawner : MonoBehaviour
{
    [System.Serializable]
    public class PropGroup
    {
        public string groupName = "Group";
        public GameObject[] prefabs;
        public int count = 10;
        public float spawnRadius = 40f;
        public float minDistanceFromCenter = 5f;
        public float minDistance = 3f;
        public bool randomRotation = true;
        [Range(0f, 0.5f)] public float scaleVariation = 0.15f;
        public float spawnHeight = 0f;
        public bool snapToGround = true;
        [Range(0f, 60f)] public float maxSteepness = 25f;
        public int maxAttempts = 30;

        [Header("━━━ ЗАЗОР ОТ РАМП ━━━")]
        [Tooltip("Если > 0, объект не заспавнится на наклонных поверхностях (рампах).")]
        [SerializeField] private float rampClearance = 0f;

        public float GetRampClearance(float globalClearance) => rampClearance > 0f ? rampClearance : globalClearance;
    }

    [Header("Группы объектов")]
    [SerializeField] private PropGroup[] propGroups;

    [Header("Ссылки")]
    [Tooltip("Объект с компонентом GridTerrainAdapter.")]
    [SerializeField] private GridTerrainAdapter gridAdapter;

    [Header("━━━ РАСПОЛОЖЕНИЕ ━━━")]
    [SerializeField, Range(0.05f, 0.35f)] private float edgeSafetyMargin = 0.15f;

    [Header("━━━ ПРОВЕРКА ОБРЫВА (Опционально) ━━━")]
    [SerializeField] private bool checkCliff = false;
    [SerializeField] private float minDistanceFromCliff = 2f;
    [SerializeField] private float maxDistanceFromCliff = 8f;
    [SerializeField] private float cliffCheckRadius = 5f;
    [SerializeField] private float minCliffHeightDiff = 2f;

    [Header("━━━ ГЛОБАЛЬНЫЙ ЗАЗОР ОТ РАМП ━━━")]
    [SerializeField] private float defaultRampClearance = 8f;

    [Header("━━━ SNAP К ЗЕМЛЕ ━━━")]
    [SerializeField] private float raycastStartHeight = 50f;
    [SerializeField] private float raycastMaxDistance = 100f;

    [Header("Отладка")]
    [SerializeField] private bool showGizmos = true;
    [SerializeField] private bool showDebugLogs = true;

    // ─── ЗАВИСИМОСТИ ──────────────────────────────────────────────────────────
    [Inject] private WorldSeedProvider _seedProvider;

    // Внутренние данные
    private readonly List<Vector3> _allSpawnedPositions = new List<Vector3>();
    private readonly Dictionary<PropGroup, List<Vector3>> _groupPositions = new Dictionary<PropGroup, List<Vector3>>();
    private int _totalSpawned = 0;
    private int _rejectedByCliff = 0;
    private int _rejectedByRamp = 0;
    private int _rejectedByAir = 0;
    private int _rejectedBySteepness = 0;
    private int _rejectedByBounds = 0;

    // Fallback (если Grid не готов)
    private Terrain _terrain;

    [Inject]
    public void Construct(WorldSeedProvider seedProvider)
    {
        _seedProvider = seedProvider;
    }

    private void Start()
    {
        // Fallback для сида
        if (_seedProvider == null)
        {
            _seedProvider = new WorldSeedProvider();
            _seedProvider.Initialize();
        }

        // Поиск адаптера
        if (gridAdapter == null)
        {
            gridAdapter = FindFirstObjectByType<GridTerrainAdapter>();
        }

        if (gridAdapter == null)
        {
            Debug.LogWarning("[PropSpawner] ⚠️ GridTerrainAdapter не найден! Пробуем fallback на старый Terrain.");
            _terrain = Terrain.activeTerrain;
            if (_terrain == null) _terrain = FindFirstObjectByType<Terrain>();
        }

        StartCoroutine(WaitAndSpawn());
    }

    private System.Collections.IEnumerator WaitAndSpawn()
    {
        // Ждем готовности Grid системы
        if (gridAdapter != null)
        {
            yield return StartCoroutine(gridAdapter.WaitForReady());
        }

        yield return null;
        SpawnAll();
    }

    public void SpawnAll()
    {
        if (propGroups == null || propGroups.Length == 0)
        {
            Debug.LogWarning("[PropSpawner] ⚠️ Нет групп объектов для спавна!");
            return;
        }

        _allSpawnedPositions.Clear();
        _groupPositions.Clear();
        _totalSpawned = 0;
        _rejectedByCliff = 0;
        _rejectedByRamp = 0;
        _rejectedByAir = 0;
        _rejectedBySteepness = 0;
        _rejectedByBounds = 0;

        System.Random rng = _seedProvider.GetRandom("props");

        foreach (var group in propGroups)
        {
            if (group.prefabs == null || group.prefabs.Length == 0) continue;

            int spawned = SpawnGroup(group, rng);
            _totalSpawned += spawned;
            if (showDebugLogs) Debug.Log($"[PropSpawner] '{group.groupName}': {spawned}/{group.count}");
        }

        Debug.Log($"[PropSpawner v2.0] ✅ Всего спавнено: {_totalSpawned}. " +
                  $"Отклонено: границы={_rejectedByBounds}, обрыв={_rejectedByCliff}, " +
                  $"рампа={_rejectedByRamp}, воздух={_rejectedByAir}, крутизна={_rejectedBySteepness}");
    }

    private int SpawnGroup(PropGroup group, System.Random rng)
    {
        int spawned = 0;
        List<Vector3> groupPositions = new List<Vector3>();
        _groupPositions[group] = groupPositions;

        float groupRampClearance = group.GetRampClearance(defaultRampClearance);

        for (int i = 0; i < group.count; i++)
        {
            Vector3 pos = FindValidPosition(group, groupPositions, rng, groupRampClearance);
            if (pos == Vector3.zero) continue;

            GameObject prefab = group.prefabs[rng.Next(group.prefabs.Length)];
            if (prefab == null) continue;

            Quaternion rotation = group.randomRotation
                ? Quaternion.Euler(0f, (float)(rng.NextDouble() * 360f), 0f)
                : Quaternion.identity;

            float scale = 1f;
            if (group.scaleVariation > 0f)
                scale = 1f + (float)(rng.NextDouble() * 2f - 1f) * group.scaleVariation;

            var obj = Instantiate(prefab, pos, rotation, transform);
            obj.transform.localScale = Vector3.one * scale;

            groupPositions.Add(pos);
            _allSpawnedPositions.Add(pos);
            spawned++;
        }
        return spawned;
    }

    private Vector3 FindValidPosition(PropGroup group, List<Vector3> groupPositions, System.Random rng, float rampClearance)
    {
        bool useGrid = gridAdapter != null && gridAdapter.IsReady;

        for (int attempt = 0; attempt < group.maxAttempts; attempt++)
        {
            // 1. Генерируем случайную позицию
            float angle = (float)(rng.NextDouble() * Mathf.PI * 2f);
            float dist = group.minDistanceFromCenter + (float)(rng.NextDouble() * (group.spawnRadius - group.minDistanceFromCenter));
            Vector3 candidate = transform.position + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);

            // 2. Проверка расстояния от других объектов
            if (IsTooClose(candidate, groupPositions, group.minDistance)) continue;

            // 3. Проверка безопасной зоны (границы карты)
            if (useGrid)
            {
                // Проверка: валидна ли позиция вообще (не стена, не пусто)
                if (!gridAdapter.IsValidPosition(candidate))
                {
                    _rejectedByBounds++;
                    continue;
                }

                // Проверка отступа от края
                Vector2 norm = gridAdapter.GetGridGenerator().GetNormalizedPosition(candidate);
                float edgeDist = Mathf.Max(Mathf.Abs(norm.x - 0.5f), Mathf.Abs(norm.y - 0.5f)) * 2f;
                float safeBoundary = 1f - edgeSafetyMargin * 2f;
                if (edgeDist > safeBoundary)
                {
                    _rejectedByBounds++;
                    continue;
                }
            }
            else if (_terrain != null)
            {
                // Fallback логика для старого Terrain
                float normX = Mathf.Clamp01((candidate.x - _terrain.transform.position.x) / _terrain.terrainData.size.x);
                float normZ = Mathf.Clamp01((candidate.z - _terrain.transform.position.z) / _terrain.terrainData.size.z);
                float edgeDist = Mathf.Max(Mathf.Abs(normX - 0.5f), Mathf.Abs(normZ - 0.5f)) * 2f;
                float safeBoundary = 1f - edgeSafetyMargin * 2f;
                if (edgeDist > safeBoundary)
                {
                    _rejectedByBounds++;
                    continue;
                }
            }

            // 4. SNAP К ЗЕМЛЕ и проверка крутизны
            float steepness = 0f;
            Vector3? groundPos = null;

            if (group.snapToGround)
            {
                groundPos = GetGroundPosition(candidate, out steepness);
                if (!groundPos.HasValue)
                {
                    _rejectedByAir++;
                    continue;
                }

                if (steepness > group.maxSteepness)
                {
                    _rejectedBySteepness++;
                    continue;
                }

                candidate = groundPos.Value + Vector3.up * group.spawnHeight;
            }
            else
            {
                candidate.y = group.spawnHeight;
                // Даже если не снэпим, нам нужно знать крутизну для проверки рамп
                steepness = useGrid ? gridAdapter.GetSteepness(candidate) : 0f;
            }

            // 5. ПРОВЕРКА РАМП (Clearance)
            // Если rampClearance > 0, мы избегаем наклонных поверхностей.
            // В Grid системе рампа = наклонная поверхность.
            // Порог 5 градусов: всё что круче, считаем рампой или стеной.
            if (rampClearance > 0f && steepness > 5f)
            {
                _rejectedByRamp++;
                continue;
            }

            // 6. Повторная проверка рампы (на всякий случай, если снап сдвинул на склон)
            // (Уже покрыто проверкой steepness выше, но оставим для ясности логики)
            if (rampClearance > 0f && steepness > 5f)
            {
                _rejectedByRamp++;
                continue;
            }

            // 7. Проверка близости к обрыву (ОПЦИОНАЛЬНО)
            if (checkCliff && !IsNearCliff(candidate))
            {
                _rejectedByCliff++;
                continue;
            }

            return candidate;
        }
        return Vector3.zero;
    }

    private Vector3? GetGroundPosition(Vector3 pos, out float steepness)
    {
        steepness = 0f;
        bool useGrid = gridAdapter != null && gridAdapter.IsReady;

        if (useGrid)
        {
            float height = gridAdapter.SampleHeight(pos);
            if (height > -100f) // Валидная высота
            {
                steepness = gridAdapter.GetSteepness(pos);
                return new Vector3(pos.x, height, pos.z);
            }
        }

        // Fallback на Terrain
        Terrain terrain = _terrain ?? Terrain.activeTerrain;
        if (terrain == null) return null;

        LayerMask groundLayer = LayerMask.GetMask("Default", "Ground", "Terrain");
        Vector3 rayOrigin = new Vector3(pos.x, terrain.transform.position.y + raycastStartHeight, pos.z);
        RaycastHit hit;

        if (Physics.Raycast(rayOrigin, Vector3.down, out hit, raycastMaxDistance, groundLayer, QueryTriggerInteraction.Ignore))
        {
            steepness = Vector3.Angle(hit.normal, Vector3.up);
            return hit.point;
        }

        float sh = terrain.SampleHeight(pos);
        if (sh > 0.01f)
        {
            steepness = terrain.terrainData.GetSteepness(
                (pos.x - terrain.transform.position.x) / terrain.terrainData.size.x,
                (pos.z - terrain.transform.position.z) / terrain.terrainData.size.z);
            return new Vector3(pos.x, sh + terrain.transform.position.y, pos.z);
        }

        return null;
    }

    private bool IsNearCliff(Vector3 pos)
    {
        bool useGrid = gridAdapter != null && gridAdapter.IsReady;
        float baseHeight;

        if (useGrid)
            baseHeight = gridAdapter.SampleHeight(pos);
        else if (_terrain != null)
            baseHeight = _terrain.SampleHeight(pos);
        else
            return true; // Если ничего нет, разрешаем спавн

        Vector2[] directions = { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) };

        foreach (var dir in directions)
        {
            Vector3 checkPos = pos + new Vector3(dir.x, 0, dir.y) * cliffCheckRadius;
            float checkHeight = useGrid ? gridAdapter.SampleHeight(checkPos) : _terrain.SampleHeight(checkPos);

            if (Mathf.Abs(checkHeight - baseHeight) >= minCliffHeightDiff)
            {
                // Если нашли перепад высот, проверяем дистанцию (упрощенно)
                // Для Grid системы достаточно того факта, что мы нашли перепад
                return true;
            }
        }
        return false;
    }

    private bool IsTooClose(Vector3 pos, List<Vector3> existing, float minDist)
    {
        foreach (var other in existing)
            if (Vector3.Distance(pos, other) < minDist) return true;
        return false;
    }

    // ─── GIZMOS ─────────────────────────────────────────────────────────────
    private void OnDrawGizmosSelected()
    {
        if (!showGizmos || propGroups == null) return;

        foreach (var group in propGroups)
        {
            Gizmos.color = new Color(1f, 0.8f, 0f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, group.spawnRadius);
        }

        Color[] groupColors = new Color[]
        {
            Color.green, new Color(0f, 0.8f, 1f), new Color(1f, 0.5f, 0f),
            new Color(0.8f, 0f, 1f), Color.yellow
        };

        int colorIndex = 0;
        foreach (var kvp in _groupPositions)
        {
            Gizmos.color = groupColors[colorIndex % groupColors.Length];
            foreach (var pos in kvp.Value)
            {
                Gizmos.DrawWireCube(pos + Vector3.up * 1f, Vector3.one * 0.5f);
            }
            colorIndex++;
        }
    }
}