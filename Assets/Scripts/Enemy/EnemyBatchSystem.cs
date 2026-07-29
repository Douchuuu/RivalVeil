using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using VContainer;

/// <summary>
/// EnemyBatchSystem — исправленная версия.
///
/// ═══════════════════════════════════════════════════════════════════
/// ФИКС E-1 — Добавлен метод UpdateSpeed():
/// ═══════════════════════════════════════════════════════════════════
///
///   ПРОБЛЕМА:
///     _speeds[index] записывался один раз в Register() и больше не обновлялся.
///     EnemyAI.SetSpeed() изменял только ai.speed, но не _speeds[index].
///     Масштабирование скорости по сложности (ApplyDifficultyToEnemy) не работало.
///
///   РЕШЕНИЕ:
///     Добавлен public void UpdateSpeed(int index, float newSpeed).
///     EnemyAI.SetSpeed() вызывает его после изменения локального поля.
///
/// ═══════════════════════════════════════════════════════════════════
/// ФИКС E-3 — SpatialSeparationJob теперь проверяет соседние ячейки:
/// ═══════════════════════════════════════════════════════════════════
///
///   ПРОБЛЕМА:
///     Старый код: `if (HashKeys[j] != myHash) continue;`
///     Хэш ячейки cx*P1 ^ cz*P2 не сохраняет пространственную близость.
///     Два врага в соседних ячейках имеют разные хэши → расталкивание не работает.
///     Враги кластеризуются вдоль границ ячеек (размер 1.5f) при радиусе 1.2f.
///
///   РЕШЕНИЕ:
///     Поле HashKeys убрано из SpatialSeparationJob.
///     Вместо этого вычисляем координаты ячейки из Position и проверяем
///     что |myCx - jCx| <= 1 AND |myCz - jCz| <= 1 (сетка 3×3).
///     Burst компилирует int-арифметику эффективно, накладные расходы минимальны.
///
/// ═══════════════════════════════════════════════════════════════════
/// ОПТИМИЗАЦИЯ E-6 — GetComponent убран из Register():
/// ═══════════════════════════════════════════════════════════════════
///
///   БЫЛО: _isMiniBoss.Add(enemy.GetComponent<MiniBossAI>() != null);
///   СТАЛО: Register принимает параметр bool isMiniBoss.
///          EnemyPool передаёт его из кэша компонентов.
///
/// ═══════════════════════════════════════════════════════════════════
/// ФИКС E-8 — UpdatePlayerPosition() вызывался ПОСЛЕ раннего выхода:
/// ═══════════════════════════════════════════════════════════════════
///
///   ПРОБЛЕМА:
///     FixedUpdate() начинался с `if (_count == 0) return;`
///     Пока врагов нет (начало волны / все убиты) — UpdatePlayerPosition()
///     не вызывался ни разу. _playerPosition[0] оставался (0, 0, 0).
///
///     Когда волна спавнит первую пачку врагов:
///       SetActive(true) → OnEnable() → Register() → _count = N > 0
///       Следующий FixedUpdate: _count > 0 → UpdatePlayerPosition() впервые.
///       Но в промежутке Job'ы уже могли использовать _playerPosition[0] = (0,0,0).
///       Враги в первый кадр двигались к началу координат, а не к игроку.
///
///   РЕШЕНИЕ:
///     Перенести UpdatePlayerPosition() ДО `if (_count == 0) return;`.
///     Позиция игрока обновляется каждый FixedUpdate независимо от числа врагов.
///     Когда первый враг регистрируется — позиция уже актуальна.
/// </summary>
public class EnemyBatchSystem : MonoBehaviour
{
    [Header("Настройки")]
    [SerializeField] private int initialCapacity = 700;
    [SerializeField] private float stopDistance = 1.0f;

    [Header("Анти-взбирание")]
    [SerializeField] private float antiClimbGravity = 40f;

    [Header("Взбирание на поверхности (ДЕФОЛТ — переопределяется в Inspector каждого врага)")]
    [Tooltip("Используется если у врага нет своих EnemyClimbSettings")]
    [SerializeField] private float stepUpForce = 10f;
    [SerializeField] private float vaultForwardBoost = 2.0f;
    [SerializeField] private float stepCheckHeight = 1.4f;
    [SerializeField] private LayerMask groundLayerMask = ~0;

    private NativeArray<float3> _positions;
    private NativeArray<float3> _velocities;
    private NativeArray<float3> _separations;
    private NativeArray<float> _speeds;
    // _spatialHashKeys оставлен только для совместимости с ComputeSpatialHashJob.
    // SpatialSeparationJob больше не использует хэши — см. ФИКС E-3.
    private NativeArray<int> _spatialHashKeys;
    private NativeArray<float3> _playerPosition;

    private int _count = 0;
    private int _capacity = 0;

    private readonly List<BaseEnemyAI> _enemies = new List<BaseEnemyAI>(700);
    private readonly List<Rigidbody> _rigidbodies = new List<Rigidbody>(700);
    private readonly Dictionary<BaseEnemyAI, int> _indexMap = new Dictionary<BaseEnemyAI, int>(700);
    private readonly List<bool> _isMiniBoss = new List<bool>(700);
    // Настройки взбирания — индивидуальные для каждого врага
    private readonly List<EnemyClimbSettings> _climbSettings = new List<EnemyClimbSettings>(700);

    private const float SEPARATION_RADIUS = 1.2f;
    private const float SEPARATION_FORCE = 2.5f;
    private const float CELL_SIZE = 1.5f;

    private const float DOWN_RAYCAST_ORIGIN = 0.5f;
    private const float DOWN_RAYCAST_DIST = 1.5f;
    private const float WALL_RAYCAST_DIST = 1.1f;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────

    private PlayerRegistry _playerRegistry;
    private PlayerStats _cachedPlayer;
    private GameStateService _gameState;
    private bool _fallbackWarningShown = false; // предупреждение о fallback — только один раз

    // ── Физический мир этой сцены (LocalPhysicsMode.Physics3D) ───────────────
    // Инициализируется в Awake(). Позволяет делать Raycast только внутри
    // своей PlayerWorldScene, не видя объекты сцены другого игрока.
    private PhysicsScene _physicsScene;

    [Inject]
    public void Construct(PlayerRegistry registry, GameStateService gameState)
    {
        _playerRegistry = registry;
        _gameState = gameState;
    }

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        _capacity = initialCapacity;
        AllocateArrays(_capacity);
        SetupEnemyPhysicsLayer();
        ExcludeLayersFromMask();
        // Кэшируем PhysicsScene этой сцены.
        // В Additive Scene режиме (LocalPhysicsMode.Physics3D) — это изолированный
        // физический мир данного игрока. В Single Player — глобальная PhysicsScene.
        _physicsScene = gameObject.scene.GetPhysicsScene();
        Debug.Log($"[EnemyBatchSystem] PhysicsScene инициализирован: {gameObject.scene.name}");
    }

    void ExcludeLayersFromMask()
    {
        int playerLayer = LayerMask.NameToLayer("Player");
        if (playerLayer >= 0) groundLayerMask &= ~(1 << playerLayer);
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (enemyLayer >= 0) groundLayerMask &= ~(1 << enemyLayer);
        int projectileLayer = LayerMask.NameToLayer("Projectile");
        if (projectileLayer >= 0) groundLayerMask &= ~(1 << projectileLayer);
    }

    void SetupEnemyPhysicsLayer()
    {
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (enemyLayer >= 0) Physics.IgnoreLayerCollision(enemyLayer, enemyLayer, true);
    }

    void OnDestroy()
    {
        DisposeArrays();
    }

    void AllocateArrays(int cap)
    {
        _positions = new NativeArray<float3>(cap, Allocator.Persistent);
        _velocities = new NativeArray<float3>(cap, Allocator.Persistent);
        _separations = new NativeArray<float3>(cap, Allocator.Persistent);
        _speeds = new NativeArray<float>(cap, Allocator.Persistent);
        _spatialHashKeys = new NativeArray<int>(cap, Allocator.Persistent);
        _playerPosition = new NativeArray<float3>(1, Allocator.Persistent);
    }

    void DisposeArrays()
    {
        if (_positions.IsCreated) _positions.Dispose();
        if (_velocities.IsCreated) _velocities.Dispose();
        if (_separations.IsCreated) _separations.Dispose();
        if (_speeds.IsCreated) _speeds.Dispose();
        if (_spatialHashKeys.IsCreated) _spatialHashKeys.Dispose();
        if (_playerPosition.IsCreated) _playerPosition.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РЕГИСТРАЦИЯ ВРАГОВ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ОПТИМИЗАЦИЯ E-6: принимает bool isMiniBoss вместо вызова GetComponent внутри.
    /// EnemyPool передаёт значение из своего кэша компонентов.
    ///
    /// ФИКС E-7 — Инициализация позиций при регистрации:
    /// ═════════════════════════════════════════════════════════
    ///   ПРОБЛЕМА:
    ///     Раньше _positions[index] оставался неинициализирован до первого FixedUpdate.
    ///     Первый FixedUpdate скопирует rb.position в _positions[index],
    ///     но в Job'ах до этого может использоваться мусорная позиция.
    ///     Результат: враг на первый кадр имеет неправильную позицию.
    ///
    ///   РЕШЕНИЕ:
    ///     Инициализировать _positions[index] = rb.position сразу в Register().
    ///     Теперь враг всегда имеет правильную позицию с момента регистрации.
    /// </summary>
    public int Register(BaseEnemyAI enemy, Rigidbody rb, float speed, bool isMiniBoss = false, EnemyClimbSettings climb = null)
    {
        if (_count >= _capacity) Expand();

        int index = _count++;
        _enemies.Add(enemy);
        _rigidbodies.Add(rb);
        _indexMap[enemy] = index;
        _speeds[index] = speed;
        _isMiniBoss.Add(isMiniBoss);

        // Индивидуальные настройки взбирания — или дефолты из Inspector EnemyBatchSystem
        _climbSettings.Add(climb ?? new EnemyClimbSettings
        {
            stepCheckHeight = stepCheckHeight,
            stepUpForce = stepUpForce,
            vaultForwardBoost = vaultForwardBoost
        });

        // ═══════════════════════════════════════════════════════════════════════════════
        // ФИКС E-7: Инициализируем позицию и velocity сразу
        // ═══════════════════════════════════════════════════════════════════════════════
        _positions[index] = rb.position;          // ← Текущая позиция Rigidbody
        _velocities[index] = rb.linearVelocity;    // ← Текущая скорость
        _separations[index] = float3.zero;           // ← Сброс разделения

        return index;
    }

    public void Unregister(BaseEnemyAI enemy)
    {
        if (!_indexMap.TryGetValue(enemy, out int index)) return;
        int last = _count - 1;

        if (index != last)
        {
            _positions[index] = _positions[last];
            _velocities[index] = _velocities[last];
            _separations[index] = _separations[last];
            _speeds[index] = _speeds[last];
            _isMiniBoss[index] = _isMiniBoss[last];
            _climbSettings[index] = _climbSettings[last];
            BaseEnemyAI lastEnemy = _enemies[last];
            _enemies[index] = lastEnemy;
            _rigidbodies[index] = _rigidbodies[last];
            _indexMap[lastEnemy] = index;

            // ФИКС: После swap _indexMap обновлён, но _batchIndex внутри lastEnemy
            // хранит устаревший индекс 'last'. Следующий вызов SetSpeed() на lastEnemy
            // уйдёт в UpdateSpeed(old_index, ...) → индекс >= _count → молчаливый дроп.
            // Обновляем _batchIndex через internal-метод, чтобы SetSpeed работал корректно.
            lastEnemy.UpdateBatchIndex(index);
        }

        _enemies.RemoveAt(last);
        _rigidbodies.RemoveAt(last);
        _indexMap.Remove(enemy);
        _isMiniBoss.RemoveAt(last);
        _climbSettings.RemoveAt(last);
        _count--;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ФИКС E-1: обновление скорости в NativeArray
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Обновляет скорость врага в NativeArray _speeds.
    /// Вызывается из EnemyAI.SetSpeed() чтобы EnemyBatchSystem применял
    /// актуальную скорость, а не ту что была при Register().
    ///
    /// Без этого: ApplyDifficultyToEnemy() → ai.SetSpeed(base * mult) → изменяло
    ///            только ai.speed, но _speeds[index] оставался базовым.
    ///            Движение всегда происходило с базовой скоростью.
    /// </summary>
    public void UpdateSpeed(int index, float newSpeed)
    {
        if (index >= 0 && index < _count)
            _speeds[index] = newSpeed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIXED UPDATE — батчинг движения
    // ─────────────────────────────────────────────────────────────────────────

    void FixedUpdate()
    {
        // ═══════════════════════════════════════════════════════════════════════
        // ФИКС E-8: UpdatePlayerPosition() вызывается ПЕРВЫМ — до любых return.
        // ═══════════════════════════════════════════════════════════════════════
        // БЫЛО:
        //   if (_count == 0) return;   ← выходили сразу
        //   ...
        //   UpdatePlayerPosition();    ← никогда не вызывалась при _count == 0
        //
        // ПРОБЛЕМА: между волнами (_count == 0) _playerPosition[0] не обновлялась.
        // Когда первые враги спавнились — их Job читал устаревшую позицию (0,0,0)
        // и они двигались в сторону начала координат вместо игрока.
        //
        // СТАЛО: позиция игрока обновляется каждый FixedUpdate независимо от
        // количества врагов. К моменту первого Register() — данные уже актуальны.
        UpdatePlayerPosition();

        if (_count == 0) return;

        if (_gameState != null && _gameState.IsPaused)
        {
            StopAllEnemies();
            return;
        }

        for (int i = 0; i < _count; i++)
            if (_rigidbodies[i] != null) _positions[i] = _rigidbodies[i].position;

        RunJobsSync();
        ApplyVelocities();
    }

    void RunJobsSync()
    {
        // ComputeSpatialHashJob оставлен для совместимости.
        // SpatialSeparationJob теперь не использует HashKeys (ФИКС E-3).
        var hashHandle = new ComputeSpatialHashJob
        {
            Positions = _positions,
            HashKeys = _spatialHashKeys,
            CellSize = CELL_SIZE,
            Count = _count
        }.Schedule(_count, 64);

        // ФИКС E-3: SpatialSeparationJob больше не принимает HashKeys.
        // Вместо этого использует координаты ячеек для проверки соседей.
        var sepHandle = new SpatialSeparationJob
        {
            Positions = _positions,
            Separations = _separations,
            SeparationRadius = SEPARATION_RADIUS,
            Count = _count,
            CellSize = CELL_SIZE
        }.Schedule(_count, 32, hashHandle);

        new EnemyMoveJob
        {
            Positions = _positions,
            Velocities = _velocities,
            Separations = _separations,
            Speeds = _speeds,
            PlayerPosition = _playerPosition,
            SeparationForce = SEPARATION_FORCE,
            StopDistance = stopDistance,
            Count = _count
        }.Schedule(_count, 64, sepHandle).Complete();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ФИЗИКА ДВИЖЕНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    Vector3 ProjectOnSurface(Vector3 vel, Vector3 normal)
    {
        Vector3 projected = Vector3.ProjectOnPlane(vel, normal);
        return projected.sqrMagnitude > 0.001f ? projected.normalized * vel.magnitude : vel;
    }

    bool DetectWall(Vector3 enemyPos, Vector3 dir, out RaycastHit bestHit)
    {
        bestHit = default;
        bool found = false;
        float highestY = float.MinValue;
        float[] heights = { 0.2f, 0.7f, 1.1f };

        foreach (float h in heights)
        {
            Vector3 origin = enemyPos + Vector3.up * h;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, WALL_RAYCAST_DIST, groundLayerMask))
            {
                if (hit.point.y > highestY)
                {
                    highestY = hit.point.y;
                    bestHit = hit;
                    found = true;
                }
            }
        }
        return found;
    }

    void ApplyVelocities()
    {
        for (int i = 0; i < _count; i++)
        {
            Rigidbody rb = _rigidbodies[i];
            if (rb == null) continue;

            BaseEnemyAI enemy = _enemies[i];
            if (enemy.IsStunned) continue;

            float3 vel3 = _velocities[i];
            float3 sep3 = _separations[i];
            Vector3 pos = rb.position;
            Vector3 playerP = new Vector3(_playerPosition[0].x, _playerPosition[0].y, _playerPosition[0].z);
            Vector3 toPlayer = playerP - pos;
            Vector3 flatDir = new Vector3(vel3.x, 0f, vel3.z).normalized;

            // НОВОЕ: Спасательный вектор лучей. Если flatDir обнулился (игрок ровно сверху), 
            // пускаем лучи туда, куда враг смотрит прямо сейчас.
            Vector3 rayDir = flatDir.sqrMagnitude > 0.001f ? flatDir : enemy.transform.forward;

            float speed = _speeds[i];
            Vector3 sep = new Vector3(sep3.x * SEPARATION_FORCE, 0f, sep3.z * SEPARATION_FORCE);

            // Индивидуальные настройки взбирания для этого врага
            EnemyClimbSettings cs = _climbSettings[i];

            // 1. Проверка земли — используем PhysicsScene этой сцены
            bool grounded = _physicsScene.Raycast(
                pos + Vector3.down * 0.9f,
                Vector3.down,
                out RaycastHit groundHit,
                0.3f,
                groundLayerMask);

            // 2. Обнаружение стены — два прохода (ИСПРАВЛЕНО ДЛЯ GRID MESH)
            //
            // ПРОБЛЕМА: обычный Raycast — бесконечно тонкий луч.
            // Когда rb.position из-за физики оказывается микроскопически внутри
            // плоскости MeshCollider, луч стартует изнутри меша и backface culling
            // возвращает false. BoxCollider обрабатывает это стабильно (имеет
            // математический объём) — отсюда "работало на кубах, сломалось на Grid".
            //
            // РЕШЕНИЕ: SphereCast — действует как труба с радиусом 0.2f.
            // Даже если pos въедет в плоскость на сотые доли миллиметра,
            // сфера физически пересечёт плоскость и вернёт уверенный хит с нормалью.
            // Микро-отступ 0.1f назад гарантирует старт снаружи меша, но достаточно
            // мал, чтобы не забросить точку старта в соседнюю клетку в углах.
            RaycastHit obstHit = default;
            bool hasObstacle = false;

            float[] checkHeights = { -0.7f, 0f, 0.7f }; // от ног до груди
            float sphereRadius = 0.2f; // Толщина луча для надёжного захвата MeshCollider
            // ФИКС 1: microOffset ОБЯЗАН быть строго больше sphereRadius (0.2f).
            // Иначе передний край сферы спавнится уже внутри стены → Unity видит
            // начальное перекрытие, игнорирует коллайдер, hasObstacle = false.
            float microOffset = 0.25f; // > sphereRadius → сфера стартует гарантированно снаружи

            // — Проход 1: направление к игроку
            foreach (float h in checkHeights)
            {
                Vector3 rayOrigin = pos + Vector3.up * h;
                Vector3 startPos = rayOrigin - rayDir * microOffset;

                if (_physicsScene.SphereCast(startPos, sphereRadius, rayDir, out obstHit, WALL_RAYCAST_DIST + microOffset, groundLayerMask))
                {
                    if (obstHit.normal.y < 0.3f) { hasObstacle = true; break; }
                }
            }

            // — Проход 2: перпендикуляр к стене из прошлого кадра (для диагонального скольжения).
            // Если игрок сбоку от стены, flatDir диагональный и Проход 1 может
            // скользнуть вдоль Grid-плоскости. transform.forward = -wallNormal
            // (установлен на прошлом кадре карабканья) бьёт строго перпендикулярно.
            if (!hasObstacle)
            {
                Vector3 fwdDir = enemy.transform.forward;
                if (Vector3.Dot(rayDir, fwdDir) < 0.85f) // >~32° расхождения
                {
                    foreach (float h in checkHeights)
                    {
                        Vector3 rayOrigin = pos + Vector3.up * h;
                        Vector3 startPos = rayOrigin - fwdDir * microOffset;

                        if (_physicsScene.SphereCast(startPos, sphereRadius, fwdDir, out obstHit, WALL_RAYCAST_DIST + microOffset, groundLayerMask))
                        {
                            if (obstHit.normal.y < 0.3f)
                            {
                                hasObstacle = true;
                                rayDir = fwdDir; // vault-чек тоже перпендикулярен стене
                                break;
                            }
                        }
                    }
                }
            }

            // 3. Проверка Vaulting (Перевал через край)
            bool isVaulting = false;
            if (hasObstacle)
            {
                // ФИКС 2: Проверяем пустоту НАД стеной на высоте cs.stepCheckHeight (~1.4f),
                // а не на уровне щиколоток (Vector3.down * 0.1f).
                // Старый луч с Vector3.down * 0.1f гарантированно врезался в стену/пол →
                // !Raycast всегда был false → isVaulting никогда не становился true.
                // Теперь луч идёт с высоты, где должно быть пустое пространство над краем.
                Vector3 vaultCheckOrigin = pos + Vector3.up * cs.stepCheckHeight;
                if (!_physicsScene.Raycast(vaultCheckOrigin, rayDir, WALL_RAYCAST_DIST + 0.5f, groundLayerMask))
                {
                    isVaulting = true;
                }
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // 🔧 ФИКС #1 (ГЛАВНЫЙ): Убираем замедление перед контактом
            // ═══════════════════════════════════════════════════════════════════════════════
            // БЫЛО: Когда враг ближе чем на stopDistance (1.0м), его скорость сбрасывалась
            //       на 15% - это создавало видимую задержку перед атакой!
            //       rb.linearVelocity = contactDir * (speed * 0.15f)  ← 85% ЗАМЕДЛЕНИЕ!
            //
            // СТАЛО: Враг движется с полной скоростью до самого контакта.
            //        OnCollisionEnter() срабатывает физической системой когда collider'ы касаются.
            //        Никаких задержек перед атакой!
            // ═══════════════════════════════════════════════════════════════════════════════
            // (это условие удалено полностью - враг не замедляется больше никогда)

            // ПРИОРИТЕТ 1: Перевал через ребро
            if (isVaulting)
            {
                // ФИКС VAULT ДЛЯ GRID:
                // Проблема была в суммировании скоростей:
                //   currentVelV.y (~5 м/с от карабканья)
                //   + AddForce(up * stepUpForce=10, Impulse) (+10 м/с)
                //   = ~15 м/с вверх → враг взлетал.
                //
                // РЕШЕНИЕ: useGravity=false (как при карабканье) + Y задаём жёстко
                // тем же значением что и при подъёме (stepUpForce*0.5f).
                // Никакого AddForce — скорости не суммируются.
                // XZ получает полный vaultForwardBoost чтобы перенести тело на платформу.
                // Как только hasObstacle станет false — ПРИОРИТЕТ 3 включит гравитацию
                // и опустит врага на поверхность.
                rb.useGravity = false;
                rb.linearVelocity = new Vector3(
                    rayDir.x * speed * cs.vaultForwardBoost + sep.x,
                    cs.stepUpForce * 0.5f,
                    rayDir.z * speed * cs.vaultForwardBoost + sep.z);
                enemy.transform.forward = rayDir;
                continue;
            }

            // ПРИОРИТЕТ 2: Карабканье
            if (hasObstacle)
            {
                // ФИКС CLIMB ДЛЯ GRID (без трения):
                // GridWorldGenerator создаёт PhysicsMaterial с friction=0.
                // Старая схема: useGravity=true + AddForce(up * stepUpForce*0.5f = 5 м/с²)
                // проигрывала гравитации (9.81 м/с²) → результат: −4.81 м/с² → враг сползал.
                // На кубе трение (μ≈0.6) компенсировало эту разницу — поэтому там работало.
                //
                // РЕШЕНИЕ: useGravity=false + Y задаём НАПРЯМУЮ через linearVelocity.
                // Гравитация отключена → никакой борьбы сил → враг гарантированно ползёт вверх.
                rb.useGravity = false;
                Vector3 wallNormal = obstHit.normal;
                rb.linearVelocity = new Vector3(
                    -wallNormal.x * speed * 0.6f + sep.x,
                    cs.stepUpForce * 0.5f,
                    -wallNormal.z * speed * 0.6f + sep.z);

                // Разворачиваем врага строго ЛИЦОМ К СТЕНЕ — лучи не соскользнут
                // вдоль ровной Grid-плоскости на следующем кадре.
                enemy.transform.forward = -wallNormal;
                continue;
            }

            // ПРИОРИТЕТ 3: Обычное движение
            if (grounded)
            {
                rb.useGravity = false;
                Vector3 moveVel = ProjectOnSurface(flatDir * speed + sep, groundHit.normal);
                rb.linearVelocity = moveVel;
                if (flatDir.sqrMagnitude > 0.01f) enemy.transform.forward = flatDir;
            }
            else
            {
                rb.useGravity = true;
                if (toPlayer.y < -0.5f)
                    rb.AddForce(Vector3.down * antiClimbGravity, ForceMode.Acceleration);

                rb.linearVelocity = new Vector3(
                    (flatDir.x * speed) + sep.x,
                    rb.linearVelocity.y,
                    (flatDir.z * speed) + sep.z);
            }
        }
    }

    void StopAllEnemies()
    {
        for (int i = 0; i < _count; i++)
            if (_rigidbodies[i] != null) _rigidbodies[i].linearVelocity = Vector3.zero;
    }

    void UpdatePlayerPosition()
    {
        PlayerStats player = _playerRegistry?.GetLocalPlayer();

        // Fallback: реестр пустой (RegisterLocalPlayer не вызвался из-за ошибки инъекции)
        // Ищем игрока напрямую по сцене и сразу регистрируем чтобы больше не искать
        if (player == null)
        {
            // ── ФИКС E-SPAWN: В SinglePlayer IsOwner = false (NGO без активного сетевого
            // сеанса). Старый код: "ps.IsOwner || !NetworkManager.Singleton.IsListening"
            // → в SP IsOwner=false и NetworkManager IS listening → условие всегда false
            // → игрок не находился → _playerPosition = spawn point навсегда.
            //
            // ФИКС: добавлена проверка GameMode.SinglePlayer + null-guard на NetworkManager.Singleton,
            // чтобы не словить NullReferenceException если NGO не инициализирован.
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool networkListening = nm != null && nm.IsListening;
            bool isSinglePlayer = GameModeManager.IsMode(GameMode.SinglePlayer);

            foreach (var ps in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                // Принимаем: владелец в Multiplayer, ИЛИ сеть не активна, ИЛИ SinglePlayer
                if (ps.IsOwner || !networkListening || isSinglePlayer)
                {
                    player = ps;
                    _playerRegistry?.RegisterLocalPlayer(ps);
                    if (!_fallbackWarningShown)
                    {
                        _fallbackWarningShown = true;
                        Debug.LogWarning($"[EnemyBatchSystem] ⚠️ Fallback: нашли игрока '{ps.name}' напрямую. " +
                                          "Это нормально при старте — игрок зарегистрирован через fallback.");
                    }
                    break;
                }
            }
        }

        if (player != null)
        {
            _cachedPlayer = player;
            _playerPosition[0] = player.transform.position;
        }
        else if (_cachedPlayer != null)
        {
            _playerPosition[0] = _cachedPlayer.transform.position;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EXPAND — корректное расширение NativeArray
    // ─────────────────────────────────────────────────────────────────────────

    void Expand()
    {
        int newCap = _capacity * 2;

        var oldPos = _positions.ToArray();
        var oldVel = _velocities.ToArray();
        var oldSep = _separations.ToArray();
        var oldSpd = _speeds.ToArray();
        var oldBoss = _isMiniBoss.ToArray();
        var oldClimb = _climbSettings.ToArray();

        DisposeArrays();
        _capacity = newCap;
        AllocateArrays(newCap);

        _isMiniBoss.Clear();
        _climbSettings.Clear();

        for (int i = 0; i < _count; i++)
        {
            _positions[i] = oldPos[i];
            _velocities[i] = oldVel[i];
            _separations[i] = oldSep[i];
            _speeds[i] = oldSpd[i];
            _isMiniBoss.Add(oldBoss[i]);
            _climbSettings.Add(oldClimb[i]);
        }

        Debug.Log($"[EnemyBatchSystem] Expand: {_capacity / 2} → {_capacity} (активных: {_count})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПУБЛИЧНЫЙ API
    // ─────────────────────────────────────────────────────────────────────────

    public void GetSpectatorSnapshot(List<EnemyGhostData> output)
    {
        output.Clear();
        for (int i = 0; i < _count; i++)
        {
            float3 p = _positions[i];
            output.Add(new EnemyGhostData
            {
                Position = new Vector3(p.x, p.y, p.z),
                IsMiniBoss = _isMiniBoss[i] ? (byte)1 : (byte)0
            });
        }
    }

    public IReadOnlyList<BaseEnemyAI> GetActiveEnemyList() => _enemies;
    public int GetActiveCount() => _count;
}

// ─────────────────────────────────────────────────────────────────────────────
// BURST JOBS
// ─────────────────────────────────────────────────────────────────────────────

[BurstCompile]
public struct ComputeSpatialHashJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> Positions;
    [WriteOnly] public NativeArray<int> HashKeys;
    [ReadOnly] public float CellSize;
    [ReadOnly] public int Count;

    public void Execute(int i)
    {
        int cx = (int)math.floor(Positions[i].x / CellSize);
        int cz = (int)math.floor(Positions[i].z / CellSize);
        HashKeys[i] = cx * 73856093 ^ cz * 19349663;
    }
}

/// <summary>
/// ФИКС E-3: SpatialSeparationJob теперь проверяет соседние ячейки (3×3 grid).
///
/// БЫЛО: проверка HashKeys[j] != myHash — пропускала врагов в соседних ячейках,
///       даже если они находились в зоне разделения (радиус 1.2 > размер ячейки / 2 = 0.75).
///
/// СТАЛО: вычисляем координаты ячейки из Positions и проверяем |Δcx| <= 1 и |Δcz| <= 1.
///        HashKeys больше не нужны здесь — они убраны из полей структуры.
/// </summary>
[BurstCompile]
public struct SpatialSeparationJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> Positions;
    [WriteOnly] public NativeArray<float3> Separations;
    [ReadOnly] public float SeparationRadius;
    [ReadOnly] public int Count;
    [ReadOnly] public float CellSize;

    public void Execute(int i)
    {
        float3 sep = float3.zero;
        float sepSq = SeparationRadius * SeparationRadius;

        // ФИКС E-3: координаты ячейки для врага i
        int myCx = (int)math.floor(Positions[i].x / CellSize);
        int myCz = (int)math.floor(Positions[i].z / CellSize);

        for (int j = 0; j < Count; j++)
        {
            if (i == j) continue;

            // ФИКС E-3: проверяем что враг j находится в соседней ячейке (±1 по X и Z)
            // Это покрывает всех врагов в зоне 3×CELL_SIZE ≈ 4.5 юнита —
            // достаточно для радиуса разделения 1.2f.
            int jCx = (int)math.floor(Positions[j].x / CellSize);
            int jCz = (int)math.floor(Positions[j].z / CellSize);

            if (math.abs(myCx - jCx) > 1 || math.abs(myCz - jCz) > 1) continue;

            float3 diff = Positions[i] - Positions[j];
            diff.y = 0;
            float distSq = math.lengthsq(diff);

            if (distSq < sepSq && distSq > 0.0001f)
            {
                float dist = math.sqrt(distSq);
                sep += (diff / dist) * ((SeparationRadius - dist) / SeparationRadius);
            }
        }

        Separations[i] = sep;
    }
}

[BurstCompile]
public struct EnemyMoveJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> Positions;
    [ReadOnly] public NativeArray<float3> Separations;
    [ReadOnly] public NativeArray<float> Speeds;
    [ReadOnly] public NativeArray<float3> PlayerPosition;
    public NativeArray<float3> Velocities;
    [ReadOnly] public float SeparationForce;
    [ReadOnly] public float StopDistance;
    [ReadOnly] public int Count;

    public void Execute(int i)
    {
        float3 toP = PlayerPosition[0] - Positions[i];
        float3 flatToP = new float3(toP.x, 0, toP.z);
        float dist = math.length(flatToP);

        // ИСПРАВЛЕНО (BUG-6): убрано искусственное замедление до 15% скорости
        // при приближении к StopDistance. Это создавало заметную задержку перед атакой.
        // Реальная остановка происходит через OnCollisionEnter/Stay в BaseEnemyAI —
        // враги бьют игрока при физическом контакте, не надо их замедлять заранее.
        Velocities[i] = (dist > 0.001f ? (flatToP / dist) : float3.zero) * Speeds[i]
                        + Separations[i] * SeparationForce;
    }
}