using UnityEngine;
using System.Collections.Generic;
using VContainer;

/// <summary>
/// GridWorldGenerator v9.0 — MEGABONK STYLE: полный рефакторинг рамп и стен
///
/// ═══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v9.0 (поверх v8.1):
///
/// BUG-1 [ШИРОКАЯ РАМПА / ДЕСЯТКИ РАМП НА ОДНОМ УТЁСЕ — Phase 1]
///   Причина: старый Phase 1 итерировал КАЖДУЮ ячейку у подножия клифа
///   и для каждой врезал каскад → N параллельных рамп рядом.
///   ИСПРАВЛЕНИЕ: run-based подход как в Phase 2.
///   - Собираем все переходы ΔH≥2 в том же направлении.
///   - Группируем по «параллельной» координате (строка клифа).
///   - Находим непрерывные отрезки (runs).
///   - На каждый отрезок — максимум 1 рампа (в центре),
///     для очень длинных (≥ 2×minRampRunLength) — 2 рампы.
///   - Введён отдельный метод TryPlaceCascadeRamp().
///
/// BUG-2 [ГОРИЗОНТАЛЬНАЯ СТЕНА ПОСРЕДИ РАМПЫ — BuildCellWalls top]
///   Причина: top = (heightLevel+1)*cellHeight для рамп.
///   Стена оказывалась высотой всего рампы и торчала сквозь её поверхность.
///   ИСПРАВЛЕНИЕ: BuildCellWalls полностью переписан.
///   - top = heightLevel * cellHeight для ЛЮБОЙ клетки (flat или ramp).
///   - BuildRampSides-треугольники покрывают baseY→topY (скошенную часть).
///   - Стены покрывают 0→baseY (подземную часть).
///   - Стены по «высокой» стороне рампы пропускаются:
///     RampEast → пропустить East, RampWest → West и т.д.
///   - Код стал в 5 раз короче и без бранчей-исключений.
///
/// BUG-3 [APPROACH CHECK СЛИШКОМ СТРОГИЙ — TryPlaceGateRamp]
///   Причина: APPROACH CHECK требовал back.type == Flat, отклоняя ячейки,
///   уже занятые Phase-1 каскадом (тип Ramp).
///   ИСПРАВЛЕНИЕ: принимаем любой тип, у которого heightLevel == h
///   (Flat ИЛИ IsRamp) как валидный подход.
///
/// BUG-4 [МАЛЕНЬКИЕ ПЛАТФОРМЫ — cellHeight слишком мала]
///   Причина: cellHeight=3f → платформы почти вровень с игроком.
///   На скриншотах Megabonk высота минимум в 2× рост персонажа.
///   ИСПРАВЛЕНИЕ: cellHeight: 3f → 5f (при cellSize=7f угол рампы ≈35.5°,
///   внутри maxRampAngle=40°).
///
/// ═══════════════════════════════════════════════════════════════════════
/// АЛГОРИТМ ПОЛНОГО ПАЙПЛАЙНА:
///   [1] GenerateHeightMap  — Box Stacking + Base Noise
///   [2] SmoothGrid(2–5)    — лёгкое сглаживание
///   [3] MarkRamps          — Phase1(ΔH≥2 каскад, run-based) + Phase2(Gate ΔH=1)
///   [4] BuildMesh
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class GridWorldGenerator : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════════
    // НАСТРОЙКИ INSPECTOR
    // ═══════════════════════════════════════════════════════════════════
    [Header("━━━ РАЗМЕРЫ СЕТКИ ━━━")]
    [SerializeField] private int gridWidth = 64;
    [SerializeField] private int gridDepth = 64;
    [Tooltip("Размер клетки. 7–8 = Megabonk масштаб.")]
    [SerializeField] private float cellSize = 7f;
    [Tooltip("Высота уровня. BUG-4 FIX: 3f→5f (платформы ≥2× рост игрока).")]
    [SerializeField] private float cellHeight = 5f;

    [Header("━━━ BOX STACKING ━━━")]
    [Tooltip("Кол-во боксов. 30–80 для 64×64.")]
    [SerializeField] private int numBoxes = 55;
    [SerializeField, Range(2, 6)] private int minBoxSize = 3;
    [SerializeField, Range(4, 16)] private int maxBoxSize = 10;
    [SerializeField, Range(1, 4)] private int minBoxHeight = 1;
    [SerializeField, Range(1, 4)] private int maxBoxHeight = 3;
    [Tooltip("Перекос к низким высотам. 0=равномерно, 1=больше L1, 2=много L1.")]
    [SerializeField, Range(0, 2)] private int heightBias = 1;

    [Header("━━━ БАЗОВЫЙ ШУМОВОЙ СЛОЙ ━━━")]
    [Tooltip("0=только боксы. 0.35=боксы+шум (Megabonk). 1=только шум.")]
    [SerializeField, Range(0f, 1f)] private float baseNoiseBlend = 0.35f;
    [SerializeField] private float noiseScale = 0.013f;
    [SerializeField] private int maxTerraceLevel = 3;

    [Header("━━━ СГЛАЖИВАНИЕ ━━━")]
    [Tooltip("2–5 проходов с боксами. Больше = мягче края.")]
    [SerializeField] private int noiseSmoothingPasses = 3;
    [SerializeField, Range(1, 4)] private int smoothingThreshold = 2;

    [Header("━━━ РАМПЫ ━━━")]
    [SerializeField] private bool enableRamps = true;
    [SerializeField, Range(15f, 45f)] private float maxRampAngle = 40f;

    [Tooltip("BUG-1 FIX: Минимальная длина прямого утёса для рампы-шлюза (Phase 2).\n" +
             "5 = только длинные обрывы получают проход.")]
    [SerializeField, Range(1, 10)] private int minRampRunLength = 5;

    [Tooltip("Плотность рамп-шлюзов Phase 2. 0=нет. 1=все.\n" +
             "0.45 = редкие проходы (Megabonk-стиль).")]
    [SerializeField, Range(0f, 1f)] private float rampGateDensity = 0.45f;

    [Tooltip("Минимальная глубина верхней платформы ЗА точкой приземления.\n" +
             "2 = после рампы должно быть ≥2 flat-клетки. Предотвращает рампы в никуда.")]
    [SerializeField, Range(1, 4)] private int minLandingCells = 2;

    [Header("━━━ АРЕНА ━━━")]
    [SerializeField] private int arenaPadding = 3;
    [Tooltip("Высота стен. Должна быть > maxTerraceLevel * cellHeight + 5.")]
    [SerializeField] private float arenaWallHeight = 30f;

    [Header("━━━ ВИЗУАЛ ━━━")]
    [Tooltip("[0]=Трава, [1]=Земля/стены, [2]=Камень/арена")]
    [SerializeField] private Material[] terrainMaterials;

    [Header("━━━ ОТЛАДКА ━━━")]
    [SerializeField] private bool autoGenerate = true;
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private bool verboseDebug = true;

    // ═══════════════════════════════════════════════════════════════════
    // ДАННЫЕ
    // ═══════════════════════════════════════════════════════════════════
    public enum CellType { Flat, RampNorth, RampSouth, RampEast, RampWest, Wall, Empty }

    public struct GridCell
    {
        public int heightLevel;
        public CellType type;
        public GridCell(int h, CellType t) { heightLevel = h; type = t; }
    }

    private GridCell[,] _grid;
    private Mesh _mesh;
    private WorldSeedProvider _seedProvider;
    private bool _isGenerated;
    private System.Random _rng;

    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;

    private float HalfW => gridWidth * cellSize * 0.5f;
    private float HalfD => gridDepth * cellSize * 0.5f;

    // Таблицы направлений (East, West, North, South)
    private static readonly int[] DDX = { 1, -1, 0, 0 };
    private static readonly int[] DDZ = { 0, 0, 1, -1 };
    private static readonly CellType[] RTYPES = {
        CellType.RampEast, CellType.RampWest,
        CellType.RampNorth, CellType.RampSouth };

    [Inject] public void Construct(WorldSeedProvider sp) => _seedProvider = sp;

    private void Awake()
    {
        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        transform.localScale = Vector3.one;
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        _meshCollider = GetComponent<MeshCollider>();
        if (_meshCollider == null) _meshCollider = gameObject.AddComponent<MeshCollider>();
        if (_seedProvider == null) { _seedProvider = new WorldSeedProvider(); _seedProvider.Initialize(); }
    }

    private void Start() { if (autoGenerate && !_isGenerated) Generate(); }

    // ═══════════════════════════════════════════════════════════════════
    // ГЛАВНЫЙ ПАЙПЛАЙН
    // ═══════════════════════════════════════════════════════════════════
    public void Generate()
    {
        if (_isGenerated) return;

        float maxPlatH = maxTerraceLevel * cellHeight;
        if (arenaWallHeight <= maxPlatH)
            Debug.LogError($"[GridWorld] ❌ arenaWallHeight={arenaWallHeight}f ≤ {maxPlatH}f!");

        _grid = new GridCell[gridWidth, gridDepth];
        GenerateHeightMap();
        SmoothGrid(noiseSmoothingPasses);
        if (enableRamps) MarkRamps();
        BuildMesh();
        _isGenerated = true;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (verboseDebug)
        {
            DebugValidateArenaWalls();
            DebugValidateGrid();
            DebugValidateMesh();
            DebugValidateRamps();
        }
#endif
        float rampAngleDeg = Mathf.Atan(cellHeight / cellSize) * Mathf.Rad2Deg;
        Debug.Log($"[GridWorld v9.0] boxes={numBoxes}({minBoxSize}–{maxBoxSize}), " +
                  $"smooth={noiseSmoothingPasses}, runLen={minRampRunLength}, " +
                  $"density={rampGateDensity:F2}, landing={minLandingCells}, " +
                  $"angle={rampAngleDeg:F1}°, cellH={cellHeight}f");
    }

    // ═══════════════════════════════════════════════════════════════════
    // ШАГ 1: BOX STACKING
    // ═══════════════════════════════════════════════════════════════════
    private void GenerateHeightMap()
    {
        for (int z = 0; z < gridDepth; z++)
            for (int x = 0; x < gridWidth; x++)
                _grid[x, z] = new GridCell(0, CellType.Flat);

        for (int z = 0; z < gridDepth; z++)
            for (int x = 0; x < gridWidth; x++)
                if (IsArenaWall(x, z)) _grid[x, z] = new GridCell(0, CellType.Wall);

        float seedOffset = _seedProvider.GetNoiseOffset("grid_terrain");
        _rng = new System.Random(Mathf.Abs(Mathf.RoundToInt(seedOffset * 100000)));

        // ─── Базовый шумовой фундамент (L0/L1) ───────────────────────────
        if (baseNoiseBlend > 0.01f)
        {
            for (int z = arenaPadding; z < gridDepth - arenaPadding; z++)
                for (int x = arenaPadding; x < gridWidth - arenaPadding; x++)
                {
                    float n = Mathf.PerlinNoise(x * noiseScale + seedOffset,
                                                 z * noiseScale + seedOffset * 1.618f);
                    _grid[x, z] = new GridCell(
                        n * baseNoiseBlend > 0.55f * baseNoiseBlend ? 1 : 0,
                        CellType.Flat);
                }
        }

        // ─── Additive Box Stacking ────────────────────────────────────────
        int innerW = gridWidth - arenaPadding * 2;
        int innerD = gridDepth - arenaPadding * 2;
        for (int i = 0; i < numBoxes; i++)
        {
            int boxW = _rng.Next(minBoxSize, maxBoxSize + 1);
            int boxD = _rng.Next(minBoxSize, maxBoxSize + 1);
            int sx = arenaPadding + _rng.Next(0, Mathf.Max(1, innerW - boxW + 1));
            int sz = arenaPadding + _rng.Next(0, Mathf.Max(1, innerD - boxD + 1));
            int h = Mathf.Clamp(RollBiasedHeight(minBoxHeight, maxBoxHeight, heightBias), 1, maxTerraceLevel);

            for (int bz = sz; bz < sz + boxD && bz < gridDepth - arenaPadding; bz++)
                for (int bx = sx; bx < sx + boxW && bx < gridWidth - arenaPadding; bx++)
                {
                    if (!IsArenaWall(bx, bz))
                        _grid[bx, bz] = new GridCell(Mathf.Max(_grid[bx, bz].heightLevel, h), CellType.Flat);
                }
        }

        for (int z = arenaPadding; z < gridDepth - arenaPadding; z++)
            for (int x = arenaPadding; x < gridWidth - arenaPadding; x++)
                _grid[x, z] = new GridCell(Mathf.Clamp(_grid[x, z].heightLevel, 0, maxTerraceLevel), CellType.Flat);
    }

    private int RollBiasedHeight(int min, int max, int bias)
    {
        int r = _rng.Next(min, max + 1);
        for (int b = 0; b < bias; b++) r = Mathf.Min(r, _rng.Next(min, max + 1));
        return r;
    }

    // ═══════════════════════════════════════════════════════════════════
    // ШАГ 2: ЛЁГКОЕ СГЛАЖИВАНИЕ
    // ═══════════════════════════════════════════════════════════════════
    private void SmoothGrid(int passes)
    {
        for (int p = 0; p < passes; p++)
        {
            var next = new GridCell[gridWidth, gridDepth];
            for (int z = 0; z < gridDepth; z++)
                for (int x = 0; x < gridWidth; x++)
                {
                    if (IsArenaWall(x, z)) { next[x, z] = _grid[x, z]; continue; }
                    int myH = _grid[x, z].heightLevel, same = 0;
                    if (x > 0 && !IsArenaWall(x - 1, z) && _grid[x - 1, z].heightLevel == myH) same++;
                    if (x < gridWidth - 1 && !IsArenaWall(x + 1, z) && _grid[x + 1, z].heightLevel == myH) same++;
                    if (z > 0 && !IsArenaWall(x, z - 1) && _grid[x, z - 1].heightLevel == myH) same++;
                    if (z < gridDepth - 1 && !IsArenaWall(x, z + 1) && _grid[x, z + 1].heightLevel == myH) same++;
                    next[x, z] = same >= (4 - smoothingThreshold)
                        ? _grid[x, z]
                        : new GridCell(DominantNeighborH(x, z), CellType.Flat);
                }
            _grid = next;
        }
    }

    private int DominantNeighborH(int x, int z)
    {
        var counts = new Dictionary<int, int>();
        int[] dx = { -1, 1, 0, 0 }, dz = { 0, 0, -1, 1 };
        for (int i = 0; i < 4; i++)
        {
            int nx = x + dx[i], nz = z + dz[i];
            if (nx >= 0 && nx < gridWidth && nz >= 0 && nz < gridDepth && !IsArenaWall(nx, nz))
                counts[_grid[nx, nz].heightLevel] = counts.TryGetValue(_grid[nx, nz].heightLevel, out int c) ? c + 1 : 1;
        }
        int bH = _grid[x, z].heightLevel, bC = -1;
        foreach (var kv in counts) if (kv.Value > bC) { bC = kv.Value; bH = kv.Key; }
        return bH;
    }

    // ═══════════════════════════════════════════════════════════════════
    // ШАГ 3: РАЗМЕТКА РАМП v9.0
    // ═══════════════════════════════════════════════════════════════════
    private void MarkRamps()
    {
        // ─── Phase 1: Каскад для ΔH ≥ 2 — RUN-BASED (BUG-1 FIX) ─────────
        //
        // Для каждого направления собираем все клетки-переходы ΔH≥2,
        // группируем по "параллельной" строке клифа, находим непрерывные
        // отрезки и ставим максимум 1–2 каскада на отрезок.
        for (int dir = 0; dir < 4; dir++)
        {
            // perpIsZ: для East(0)/West(1) рампа тянется вдоль Z
            //          для North(2)/South(3) рампа тянется вдоль X
            bool perpIsZ = (dir < 2);
            var byParallel = new Dictionary<int, List<int>>();

            for (int z = arenaPadding + 1; z < gridDepth - arenaPadding - 1; z++)
                for (int x = arenaPadding + 1; x < gridWidth - arenaPadding - 1; x++)
                {
                    if (IsArenaWall(x, z) || _grid[x, z].type != CellType.Flat) continue;
                    int h = _grid[x, z].heightLevel;
                    int nx = x + DDX[dir], nz = z + DDZ[dir];
                    if (!InnerBounds(nx, nz) || IsArenaWall(nx, nz)) continue;
                    if (_grid[nx, nz].type != CellType.Flat) continue;
                    int delta = _grid[nx, nz].heightLevel - h;
                    if (delta < 2) continue;

                    int parallel = perpIsZ ? x : z;
                    int perp = perpIsZ ? z : x;
                    if (!byParallel.ContainsKey(parallel)) byParallel[parallel] = new List<int>();
                    byParallel[parallel].Add(perp);
                }

            foreach (var kv in byParallel)
            {
                int parallel = kv.Key;
                var perps = kv.Value;
                perps.Sort();

                int runStart = 0;
                for (int i = 1; i <= perps.Count; i++)
                {
                    bool isEnd = (i == perps.Count) || (perps[i] - perps[i - 1] > 1);
                    if (!isEnd) continue;

                    int runLen = i - runStart;

                    // 1 рампа в центре отрезка
                    TryPlaceCascadeRamp(perps[runStart + runLen / 2], parallel, perpIsZ, dir);

                    // 2-я рампа для очень длинных отрезков
                    if (runLen >= minRampRunLength * 2)
                        TryPlaceCascadeRamp(perps[runStart + runLen * 3 / 4], parallel, perpIsZ, dir);

                    runStart = i;
                }
            }
        }

        // ─── Phase 2: Gate Ramps для ΔH = 1 ─────────────────────────────
        //
        // BUG-1 FIX: run-based отбор (из v8.1).
        // BUG-3 FIX: APPROACH CHECK принимает IsRamp как валидный подход.
        float rampSeed = _seedProvider.GetNoiseOffset("ramp_gate");

        for (int dir = 0; dir < 4; dir++)
        {
            bool perpIsZ = (dir < 2);
            var byParallel = new Dictionary<int, List<int>>();

            for (int z = arenaPadding; z < gridDepth - arenaPadding; z++)
                for (int x = arenaPadding; x < gridWidth - arenaPadding; x++)
                {
                    if (IsArenaWall(x, z) || _grid[x, z].type != CellType.Flat) continue;
                    int h = _grid[x, z].heightLevel;
                    int nx = x + DDX[dir], nz = z + DDZ[dir];
                    if (!InnerBounds(nx, nz) || IsArenaWall(nx, nz)) continue;
                    if (_grid[nx, nz].type != CellType.Flat) continue;
                    if (_grid[nx, nz].heightLevel != h + 1) continue;

                    int parallel = perpIsZ ? x : z;
                    int perp = perpIsZ ? z : x;
                    if (!byParallel.ContainsKey(parallel)) byParallel[parallel] = new List<int>();
                    byParallel[parallel].Add(perp);
                }

            foreach (var kv in byParallel)
            {
                int parallel = kv.Key;
                var perps = kv.Value;
                perps.Sort();

                int runStart = 0;
                for (int i = 1; i <= perps.Count; i++)
                {
                    bool isEnd = (i == perps.Count) || (perps[i] - perps[i - 1] > 1);
                    if (!isEnd) continue;

                    int runLen = i - runStart;
                    if (runLen >= minRampRunLength)
                    {
                        TryPlaceGateRamp(perps[runStart + runLen / 2], parallel,
                                         perpIsZ, dir, rampSeed);

                        if (runLen >= minRampRunLength * 3)
                            TryPlaceGateRamp(perps[runStart + runLen * 3 / 4], parallel,
                                             perpIsZ, dir, rampSeed + 5.3f);
                    }
                    runStart = i;
                }
            }
        }
    }

    // ─── Phase 1: размещение каскадной рампы (BUG-1 FIX) ───────────────
    /// <summary>
    /// Врезает каскад из delta рамп-ячеек в тело высокой платформы.
    /// Вызывается из run-based Phase 1 — максимум 1–2 раза на отрезок клифа.
    /// </summary>
    private void TryPlaceCascadeRamp(int perp, int parallel, bool perpIsZ, int dir)
    {
        int rx = perpIsZ ? parallel : perp;
        int rz = perpIsZ ? perp : parallel;

        if (!InnerBounds(rx, rz) || IsArenaWall(rx, rz)) return;
        if (_grid[rx, rz].type != CellType.Flat) return;

        int h = _grid[rx, rz].heightLevel;
        int nx = rx + DDX[dir], nz = rz + DDZ[dir];
        if (!InnerBounds(nx, nz) || IsArenaWall(nx, nz)) return;
        if (_grid[nx, nz].type != CellType.Flat) return;

        int delta = _grid[nx, nz].heightLevel - h;
        if (delta < 2) return;

        // Проверяем delta ячеек платформы (все должны быть Flat на уровне h+delta)
        for (int step = 0; step < delta; step++)
        {
            int cx = nx + step * DDX[dir], cz = nz + step * DDZ[dir];
            if (!InnerBounds(cx, cz) || IsArenaWall(cx, cz)) return;
            var c = _grid[cx, cz];
            if (c.type != CellType.Flat || c.heightLevel < h + delta) return;
        }

        // BUG-4 FIX (из v8.1): проверяем minLandingCells клеток ЗА концом каскада
        for (int step = 0; step < minLandingCells; step++)
        {
            int lx = nx + (delta + step) * DDX[dir];
            int lz = nz + (delta + step) * DDZ[dir];
            if (!InnerBounds(lx, lz) || IsArenaWall(lx, lz)) return;
            var lc = _grid[lx, lz];
            if (lc.type != CellType.Flat || lc.heightLevel != h + delta) return;
        }

        // Врезаем каскад: каждый step-шаг — одна рамп-ячейка
        for (int step = 0; step < delta; step++)
            _grid[nx + step * DDX[dir], nz + step * DDZ[dir]] =
                new GridCell(h + step, RTYPES[dir]);
    }

    // ─── Phase 2: размещение шлюзовой рампы (ΔH=1) ─────────────────────
    /// <summary>
    /// Попытка разместить рампу-шлюз с полной валидацией.
    /// BUG-3 FIX: APPROACH CHECK теперь принимает IsRamp как валидный подход.
    /// </summary>
    private void TryPlaceGateRamp(int perp, int parallel, bool perpIsZ, int dir, float noiseSeed)
    {
        int rx = perpIsZ ? parallel : perp;
        int rz = perpIsZ ? perp : parallel;

        if (!InnerBounds(rx, rz) || IsArenaWall(rx, rz)) return;
        if (_grid[rx, rz].type != CellType.Flat) return;

        int h = _grid[rx, rz].heightLevel;
        int nx = rx + DDX[dir], nz = rz + DDZ[dir];

        if (!InnerBounds(nx, nz) || IsArenaWall(nx, nz)) return;
        if (_grid[nx, nz].type != CellType.Flat) return;
        if (_grid[nx, nz].heightLevel != h + 1) return;

        // ── APPROACH CHECK (BUG-3 FIX): принимаем Flat ИЛИ IsRamp на уровне h ──
        int bx = rx - DDX[dir], bz = rz - DDZ[dir];
        if (InnerBounds(bx, bz) && !IsArenaWall(bx, bz))
        {
            var back = _grid[bx, bz];
            // Высота обязана совпадать с h (нижний уровень)
            if (back.heightLevel != h) return;
            // Тип: Flat ИЛИ рампа (Phase 1 каскад мог оставить рампу здесь)
            if (back.type != CellType.Flat && !IsRamp(back.type)) return;
        }

        // ── LANDING CHECK: ≥ minLandingCells flat/ramp клеток на h+1 за рампой ──
        for (int step = 1; step <= minLandingCells; step++)
        {
            int lx = nx + step * DDX[dir];
            int lz = nz + step * DDZ[dir];
            if (!InnerBounds(lx, lz) || IsArenaWall(lx, lz)) return;
            var lc = _grid[lx, lz];
            if (lc.heightLevel != h + 1) return;
            if (lc.type != CellType.Flat && !IsRamp(lc.type)) return;
        }

        // ── NOISE GATE: rampGateDensity контролирует финальную плотность ──
        float noiseVal = Mathf.PerlinNoise(rx * 0.3f + noiseSeed, rz * 0.3f + noiseSeed * 1.3f);
        float threshold = 1f - rampGateDensity;
        if (noiseVal < threshold) return;

        // Всё проверено — ставим рампу
        _grid[rx, rz] = new GridCell(h, RTYPES[dir]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ШАГ 4: ПОСТРОЕНИЕ МЕША
    // ═══════════════════════════════════════════════════════════════════
    private void BuildMesh()
    {
        var V = new List<Vector3>();
        var N = new List<Vector3>();
        var UV = new List<Vector2>();
        var T0 = new List<int>();    // трава
        var T1 = new List<int>();    // земля/стены
        var T2 = new List<int>();    // арена

        for (int z = 0; z < gridDepth; z++) for (int x = 0; x < gridWidth; x++)
        {
            var c = _grid[x, z];
            if (c.type == CellType.Empty || c.type == CellType.Wall || IsArenaWall(x, z)) continue;
            BuildCellTop(x, z, c, V, T0, T1, UV, N);
            BuildCellWalls(x, z, c, V, T1, UV, N);
        }
        BuildArenaWalls(V, T2, UV, N);

        _mesh = new Mesh
        {
            name = "GridWorldMesh_v9.0",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        _mesh.vertices = V.ToArray(); _mesh.normals = N.ToArray(); _mesh.uv = UV.ToArray();
        _mesh.subMeshCount = 3;
        _mesh.SetTriangles(T0, 0); _mesh.SetTriangles(T1, 1); _mesh.SetTriangles(T2, 2);
        _mesh.RecalculateBounds();

        if (_meshFilter) _meshFilter.sharedMesh = _mesh;
        if (_meshRenderer && terrainMaterials?.Length >= 3)
            _meshRenderer.sharedMaterials = terrainMaterials;
        if (_meshCollider)
        {
            _meshCollider.convex = false;
            _meshCollider.sharedMesh = null;
            _meshCollider.sharedMesh = _mesh;

            // НОВОЕ: Создаем и применяем физический материал без трения
            var pm = new PhysicsMaterial("GridFrictionless") {
                dynamicFriction = 0f,
                staticFriction = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum
            };
            _meshCollider.sharedMaterial = pm;
        }
    }

    // ─── Верхняя поверхность клетки ─────────────────────────────────────
    private void BuildCellTop(int x, int z, GridCell cell,
        List<Vector3> V, List<int> T0, List<int> T1, List<Vector2> UV, List<Vector3> N)
    {
        float hs = cellSize * 0.5f, cx = x * cellSize - HalfW + hs, cz = z * cellSize - HalfD + hs;
        float baseY = (cell.heightLevel == 0 && cell.type == CellType.Flat)
                      ? 0.05f : cell.heightLevel * cellHeight;
        float topY = baseY + cellHeight;

        var v0 = new Vector3(cx - hs, baseY, cz - hs); // SW
        var v1 = new Vector3(cx + hs, baseY, cz - hs); // SE
        var v2 = new Vector3(cx + hs, baseY, cz + hs); // NE
        var v3 = new Vector3(cx - hs, baseY, cz + hs); // NW

        switch (cell.type)
        {
            case CellType.RampEast: v1.y = topY; v2.y = topY; break;
            case CellType.RampWest: v0.y = topY; v3.y = topY; break;
            case CellType.RampNorth: v2.y = topY; v3.y = topY; break;
            case CellType.RampSouth: v0.y = topY; v1.y = topY; break;
        }
        // Winding order → нормаль вверх
        AddQuad(V, T0, UV, N, v0, v3, v2, v1, Vector3.Cross(v3 - v0, v1 - v0).normalized);

        if (cell.type != CellType.Flat)
            BuildRampSides(cell, v0, v1, v2, v3, baseY, V, T1, UV, N);
    }

    private void BuildRampSides(GridCell cell,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, float baseY,
        List<Vector3> V, List<int> T, List<Vector2> UV, List<Vector3> N)
    {
        switch (cell.type)
        {
            case CellType.RampEast:
                // South triangle: SW(base)→SE(top)→SE(base)
                AddTri(V, T, UV, N, v0, v1, new Vector3(v1.x, baseY, v1.z), Vector3.back);
                // North triangle: NW(base)→NE(base)→NE(top)
                AddTri(V, T, UV, N, v3, new Vector3(v2.x, baseY, v2.z), v2, Vector3.forward);
                break;
            case CellType.RampWest:
                // South triangle: SW(top)→SE(base)→SW(base)
                AddTri(V, T, UV, N, v0, v1, new Vector3(v0.x, baseY, v0.z), Vector3.back);
                // North triangle: NE(base)→NW(top)→NW(base)
                AddTri(V, T, UV, N, v2, v3, new Vector3(v3.x, baseY, v3.z), Vector3.forward);
                break;
            case CellType.RampNorth:
                // West triangle: SW(base)→NW(base)→NW(top)
                AddTri(V, T, UV, N, v0, new Vector3(v3.x, baseY, v3.z), v3, Vector3.left);
                // East triangle: SE(base)→NE(top)→NE(base)
                AddTri(V, T, UV, N, v1, v2, new Vector3(v2.x, baseY, v2.z), Vector3.right);
                break;
            case CellType.RampSouth:
                // West triangle: NW(base)→SW(base)→SW(top)
                AddTri(V, T, UV, N, v3, v0, new Vector3(v0.x, baseY, v0.z), Vector3.left);
                // East triangle: NE(base)→SE(base)→SE(top) — ждём нормаль right
                AddTri(V, T, UV, N, v2, new Vector3(v1.x, baseY, v1.z), v1, Vector3.right);
                break;
        }
    }

    // ─── Вертикальные стены между клетками (BUG-2 FIX) ──────────────────
    //
    // КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ v9.0:
    // top = cell.heightLevel * cellHeight  ← для ЛЮБОЙ клетки (flat или ramp)
    //
    // Для плоских клеток: стена от 0 до h*cellHeight = поверхность клетки ✓
    // Для рамп:           стена от 0 до baseY (нижний конец рампы) ✓
    //   Скошенная часть (baseY → topY) покрыта треугольниками BuildRampSides.
    //   Вместе они образуют геометрически замкнутую поверхность без дыр.
    //
    // Стена по «высокой» стороне рампы пропускается:
    //   RampEast  → пропустить East-стену  (там верхний край рампы)
    //   RampWest  → пропустить West-стену
    //   RampNorth → пропустить North-стену
    //   RampSouth → пропустить South-стену
    private void BuildCellWalls(int x, int z, GridCell cell,
        List<Vector3> V, List<int> T, List<Vector2> UV, List<Vector3> N)
    {
        float hs = cellSize * 0.5f;
        float cx = x * cellSize - HalfW + hs;
        float cz = z * cellSize - HalfD + hs;

        // top = высота «нижнего» конца поверхности (одинакова для flat и ramp)
        float top = cell.heightLevel * cellHeight;

        // L0 плоские клетки: нет соседей ниже → стены не нужны
        if (top <= 0.01f) return;

        int nH;

        // ── East стена (пропустить для RampEast — там высокий конец) ──────
        if (cell.type != CellType.RampEast)
        {
            nH = NH(x + 1, z);
            if (nH < cell.heightLevel)
                WF(V, T, UV, N,
                    new(cx + hs, top, cz - hs), new(cx + hs, top, cz + hs),
                    new(cx + hs, 0f, cz + hs), new(cx + hs, 0f, cz - hs), Vector3.right);
        }

        // ── West стена (пропустить для RampWest) ─────────────────────────
        if (cell.type != CellType.RampWest)
        {
            nH = NH(x - 1, z);
            if (nH < cell.heightLevel)
                WF(V, T, UV, N,
                    new(cx - hs, top, cz + hs), new(cx - hs, top, cz - hs),
                    new(cx - hs, 0f, cz - hs), new(cx - hs, 0f, cz + hs), Vector3.left);
        }

        // ── South стена (пропустить для RampSouth) ───────────────────────
        if (cell.type != CellType.RampSouth)
        {
            nH = NH(x, z - 1);
            if (nH < cell.heightLevel)
                WF(V, T, UV, N,
                    new(cx - hs, top, cz - hs), new(cx + hs, top, cz - hs),
                    new(cx + hs, 0f, cz - hs), new(cx - hs, 0f, cz - hs), Vector3.back);
        }

        // ── North стена (пропустить для RampNorth) ───────────────────────
        if (cell.type != CellType.RampNorth)
        {
            nH = NH(x, z + 1);
            if (nH < cell.heightLevel)
                WF(V, T, UV, N,
                    new(cx + hs, top, cz + hs), new(cx - hs, top, cz + hs),
                    new(cx - hs, 0f, cz + hs), new(cx + hs, 0f, cz + hs), Vector3.forward);
        }
    }

    // ─── Стены арены ────────────────────────────────────────────────────
    private void BuildArenaWalls(List<Vector3> V, List<int> T, List<Vector2> UV, List<Vector3> N)
    {
        float top = arenaWallHeight, bot = 0f, hw = HalfW, hd = HalfD;
        float iw = hw - arenaPadding * cellSize, id = hd - arenaPadding * cellSize;

        WF(V, T, UV, N, new(-iw, top, -hd), new(-iw, top, +hd), new(-iw, bot, +hd), new(-iw, bot, -hd), Vector3.right);
        WF(V, T, UV, N, new(+iw, top, +hd), new(+iw, top, -hd), new(+iw, bot, -hd), new(+iw, bot, +hd), Vector3.left);
        WF(V, T, UV, N, new(+iw, top, -id), new(-iw, top, -id), new(-iw, bot, -id), new(+iw, bot, -id), Vector3.forward);
        WF(V, T, UV, N, new(-iw, top, +id), new(+iw, top, +id), new(+iw, bot, +id), new(-iw, bot, +id), Vector3.back);

        AddQuad(V, T, UV, N, new(-hw, top, -hd), new(-hw, top, +hd), new(-iw, top, +hd), new(-iw, top, -hd), Vector3.up);
        AddQuad(V, T, UV, N, new(+iw, top, -hd), new(+iw, top, +hd), new(+hw, top, +hd), new(+hw, top, -hd), Vector3.up);
        AddQuad(V, T, UV, N, new(-iw, top, -hd), new(-iw, top, -id), new(+iw, top, -id), new(+iw, top, -hd), Vector3.up);
        AddQuad(V, T, UV, N, new(-iw, top, +id), new(-iw, top, +hd), new(+iw, top, +hd), new(+iw, top, +id), Vector3.up);
    }

    // ─── Примитивы меша ─────────────────────────────────────────────────
    private void AddQuad(List<Vector3> V, List<int> T, List<Vector2> UV, List<Vector3> N,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 n)
    {
        int s = V.Count; V.Add(v0); V.Add(v1); V.Add(v2); V.Add(v3);
        N.Add(n); N.Add(n); N.Add(n); N.Add(n);
        UV.Add(new(0, 0)); UV.Add(new(1, 0)); UV.Add(new(1, 1)); UV.Add(new(0, 1));
        T.Add(s); T.Add(s + 1); T.Add(s + 2); T.Add(s); T.Add(s + 2); T.Add(s + 3);
    }
    private void AddTri(List<Vector3> V, List<int> T, List<Vector2> UV, List<Vector3> N,
        Vector3 a, Vector3 b, Vector3 c, Vector3 n)
    {
        int s = V.Count; V.Add(a); V.Add(b); V.Add(c);
        N.Add(n); N.Add(n); N.Add(n);
        UV.Add(new(0, 0)); UV.Add(new(1, 0)); UV.Add(new(0.5f, 1));
        T.Add(s); T.Add(s + 1); T.Add(s + 2);
    }
    private void WF(List<Vector3> V, List<int> T, List<Vector2> UV, List<Vector3> N,
        Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, Vector3 n)
    {
        int s = V.Count; V.Add(tl); V.Add(tr); V.Add(br); V.Add(bl);
        N.Add(n); N.Add(n); N.Add(n); N.Add(n);
        float w = Vector3.Distance(new(tl.x, 0, tl.z), new(tr.x, 0, tr.z)) / cellSize;
        float h = (tl.y - bl.y) / cellSize;
        UV.Add(new(0, h)); UV.Add(new(w, h)); UV.Add(new(w, 0)); UV.Add(new(0, 0));
        T.Add(s); T.Add(s + 1); T.Add(s + 2); T.Add(s); T.Add(s + 2); T.Add(s + 3);
    }

    // ═══════════════════════════════════════════════════════════════════
    // УТИЛИТЫ
    // ═══════════════════════════════════════════════════════════════════
    /// <summary>Высота соседа или -1 если вне сетки.</summary>
    private int NH(int x, int z) =>
        (x < 0 || x >= gridWidth || z < 0 || z >= gridDepth) ? -1 : _grid[x, z].heightLevel;

    /// <summary>Внутри игровой зоны (с учётом arenaPadding).</summary>
    private bool InnerBounds(int x, int z) =>
        x >= arenaPadding && x < gridWidth - arenaPadding &&
        z >= arenaPadding && z < gridDepth - arenaPadding;

    private bool IsArenaWall(int x, int z) =>
        x < arenaPadding || x >= gridWidth - arenaPadding || z < arenaPadding || z >= gridDepth - arenaPadding;

    private static bool IsRamp(CellType t) =>
        t == CellType.RampEast || t == CellType.RampWest || t == CellType.RampNorth || t == CellType.RampSouth;

    // ═══════════════════════════════════════════════════════════════════
    // DEBUG
    // ═══════════════════════════════════════════════════════════════════
    private void DebugValidateMesh()
    {
        if (_mesh == null || _mesh.vertexCount == 0) { Debug.LogError("[GridWorld] ❌ Меш пустой!"); return; }
        if (_meshCollider?.sharedMesh == null) { Debug.LogError("[GridWorld] ❌ Нет коллайдера!"); return; }
        if (_meshCollider.convex) { _meshCollider.convex = false; Debug.LogWarning("[GridWorld] convex→false"); }
        Debug.Log($"[GridWorld] ✅ Меш: {_mesh.vertexCount} вершин");
    }
    private void DebugValidateGrid()
    {
        if (_grid == null) { Debug.LogError("[GridWorld] ❌ _grid==null!"); return; }
        int flat = 0, ramp = 0, wall = 0;
        int[] lc = new int[maxTerraceLevel + 1];
        for (int z = 0; z < gridDepth; z++) for (int x = 0; x < gridWidth; x++)
        {
            var c = _grid[x, z];
            if (c.type == CellType.Flat) flat++;
            else if (c.type == CellType.Wall) wall++;
            else if (IsRamp(c.type)) { ramp++; if (!IsArenaWall(x, z)) lc[Mathf.Clamp(c.heightLevel, 0, maxTerraceLevel)]++; }
            if (!IsArenaWall(x, z) && c.type == CellType.Flat) lc[Mathf.Clamp(c.heightLevel, 0, maxTerraceLevel)]++;
        }
        string ls = ""; for (int l = 0; l <= maxTerraceLevel; l++) ls += $"L{l}={lc[l]} ";
        Debug.Log($"[GridWorld] 📊 Flat={flat} Ramp={ramp} Wall={wall} | {ls}");
    }
    private void DebugValidateArenaWalls()
    {
        float mp = maxTerraceLevel * cellHeight;
        if (arenaWallHeight <= mp) Debug.LogError($"[GridWorld] ❌ arenaWallHeight={arenaWallHeight}≤{mp}!");
        else Debug.Log($"[GridWorld] ✅ Стены OK (arenaWall={arenaWallHeight}, maxPlat={mp})");
    }
    private void DebugValidateRamps()
    {
        int total = 0, noLanding = 0;
        for (int z = 1; z < gridDepth - 1; z++) for (int x = 1; x < gridWidth - 1; x++)
        {
            if (!IsRamp(_grid[x, z].type)) continue; total++;
            int h = _grid[x, z].heightLevel; bool hasHigh = false;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + DDX[d], nz = z + DDZ[d];
                if (nx >= 0 && nx < gridWidth && nz >= 0 && nz < gridDepth && _grid[nx, nz].heightLevel > h) hasHigh = true;
            }
            if (!hasHigh) noLanding++;
        }
        Debug.Log($"[GridWorld] 🔺 Рампы: всего={total}, без высокого соседа={noLanding}");
    }
    [ContextMenu("Force Regenerate")] private void EditorRegen() => ForceRegenerate();
    [ContextMenu("Debug: Raycast")]
    private void DebugRaycast()
    {
        if (!_isGenerated) { Debug.LogWarning("[GridWorld] Не сгенерирован!"); return; }
        int hit = 0, miss = 0;
        for (int i = 0; i < 200; i++)
        {
            float rx = Random.Range(0f, gridWidth * cellSize) - HalfW;
            float rz = Random.Range(0f, gridDepth * cellSize) - HalfD;
            if (Physics.Raycast(new(rx, arenaWallHeight + 5f, rz), Vector3.down, arenaWallHeight + 10f)) hit++;
            else { miss++; if (miss <= 5) Debug.LogWarning($"[GridWorld] 🕳️({rx:F1},{rz:F1})"); }
        }
        Debug.Log($"[GridWorld] 🔍 {hit}/200, дыр={miss}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // RESET / REGENERATE
    // ═══════════════════════════════════════════════════════════════════
    public void Reset()
    {
        _isGenerated = false; _grid = null;
        if (_mesh != null) { if (_meshFilter) _meshFilter.sharedMesh = null; if (_meshCollider) _meshCollider.sharedMesh = null; Destroy(_mesh); _mesh = null; }
    }
    public void ForceRegenerate() { Reset(); Generate(); }

    // ═══════════════════════════════════════════════════════════════════
    // ПУБЛИЧНЫЙ API
    // ═══════════════════════════════════════════════════════════════════
    public int GridWidth => gridWidth;
    public int GridDepth => gridDepth;
    public float CellSize => cellSize;
    public float CellHeight => cellHeight;
    public int MaxTerraceLevel => maxTerraceLevel;
    public bool IsGenerated => _isGenerated;

    public bool IsCellWalkable(int gx, int gz)
    {
        if (gx < 0 || gx >= gridWidth || gz < 0 || gz >= gridDepth) return false;
        var t = _grid[gx, gz].type; return t == CellType.Flat || IsRamp(t);
    }
    public bool IsWalkable(Vector3 wp)
    {
        if (!_isGenerated) return false;
        var gp = WorldToGrid(wp); return IsCellWalkable(gp.x, gp.y) && GetSteepness(wp) <= 45f;
    }
    public Vector2Int WorldToGrid(Vector3 wp)
    {
        var lp = transform.InverseTransformPoint(wp);
        return new(Mathf.FloorToInt((lp.x + HalfW) / cellSize), Mathf.FloorToInt((lp.z + HalfD) / cellSize));
    }
    public Vector2 GetNormalizedPosition(Vector3 wp)
    {
        var lp = transform.InverseTransformPoint(wp);
        return new(Mathf.Clamp01((lp.x + HalfW) / (gridWidth * cellSize)),
                   Mathf.Clamp01((lp.z + HalfD) / (gridDepth * cellSize)));
    }
    public Vector3 GridToWorld(int gx, int gz)
    {
        float lx = gx * cellSize - HalfW + cellSize * 0.5f, lz = gz * cellSize - HalfD + cellSize * 0.5f;
        return transform.TransformPoint(new(lx, GetSurfaceHeight(transform.TransformPoint(new(lx, 0, lz))), lz));
    }
    public float GetSurfaceHeight(Vector3 wp)
    {
        var lp = transform.InverseTransformPoint(wp);
        int gx = Mathf.FloorToInt((lp.x + HalfW) / cellSize), gz = Mathf.FloorToInt((lp.z + HalfD) / cellSize);
        if (gx < 0 || gx >= gridWidth || gz < 0 || gz >= gridDepth) return -999f;
        var cell = _grid[gx, gz];
        if (IsRamp(cell.type))
        {
            float ccx = gx * cellSize - HalfW + cellSize * 0.5f, ccz = gz * cellSize - HalfD + cellSize * 0.5f;
            float lx = (lp.x - ccx) / cellSize + 0.5f, lz2 = (lp.z - ccz) / cellSize + 0.5f;
            float bY = cell.heightLevel * cellHeight, tY = bY + cellHeight;
            return cell.type switch
            {
                CellType.RampEast => Mathf.Lerp(bY, tY, Mathf.Clamp01(lx)),
                CellType.RampWest => Mathf.Lerp(tY, bY, Mathf.Clamp01(lx)),
                CellType.RampNorth => Mathf.Lerp(bY, tY, Mathf.Clamp01(lz2)),
                CellType.RampSouth => Mathf.Lerp(tY, bY, Mathf.Clamp01(lz2)),
                _ => bY
            };
        }
        return cell.heightLevel == 0 ? 0.05f : cell.heightLevel * cellHeight;
    }
    public float GetSteepness(Vector3 wp)
    {
        var gp = WorldToGrid(wp);
        if (gp.x < 1 || gp.x >= gridWidth - 1 || gp.y < 1 || gp.y >= gridDepth - 1) return 0f;
        if (IsRamp(_grid[gp.x, gp.y].type)) return maxRampAngle;
        float sx = Mathf.Abs(_grid[gp.x + 1, gp.y].heightLevel - _grid[gp.x - 1, gp.y].heightLevel) * cellHeight / (cellSize * 2f);
        float sz = Mathf.Abs(_grid[gp.x, gp.y + 1].heightLevel - _grid[gp.x, gp.y - 1].heightLevel) * cellHeight / (cellSize * 2f);
        return Mathf.Atan(Mathf.Sqrt(sx * sx + sz * sz)) * Mathf.Rad2Deg;
    }
    public Vector3 GetWorldSize() => new(gridWidth * cellSize, Mathf.Max(maxTerraceLevel * cellHeight, arenaWallHeight), gridDepth * cellSize);
    public Vector3 GetWorldCenter() => transform.position;

    // ═══════════════════════════════════════════════════════════════════
    // GIZMOS (цвет по уровню: L0=зелёный → L3=красный; рампы=голубой)
    // ═══════════════════════════════════════════════════════════════════
    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || _grid == null) return;
        Color[] lc = { new(0.2f, 0.9f, 0.2f), new(0.8f, 0.9f, 0.1f), new(0.9f, 0.6f, 0.1f), new(0.9f, 0.2f, 0.1f) };
        for (int z = 0; z < gridDepth; z++) for (int x = 0; x < gridWidth; x++)
        {
            var cell = _grid[x, z];
            if (cell.type == CellType.Empty || cell.type == CellType.Wall) continue;
            float cx2 = x * cellSize - HalfW + cellSize * 0.5f, cz2 = z * cellSize - HalfD + cellSize * 0.5f;
            float cy2 = cell.heightLevel * cellHeight + transform.position.y;
            Color c = IsRamp(cell.type) ? Color.cyan : lc[Mathf.Clamp(cell.heightLevel, 0, lc.Length - 1)];
            Gizmos.color = new(c.r, c.g, c.b, 0.3f);
            Gizmos.DrawCube(new(cx2, cy2 + cellHeight * 0.5f, cz2), new(cellSize, cellHeight, cellSize));
        }
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(transform.position + Vector3.up * arenaWallHeight * 0.5f,
            new(gridWidth * cellSize, arenaWallHeight, gridDepth * cellSize));
    }
}