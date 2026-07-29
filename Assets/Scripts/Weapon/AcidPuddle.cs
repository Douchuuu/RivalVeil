using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// ACID PUDDLE — кислотная лужа (ИСПРАВЛЕННАЯ ВЕРСИЯ)
/// ══════════════════════════════════════════════════════════════════════
///
/// ИСПРАВЛЕНИЯ ПРОИЗВОДИТЕЛЬНОСТИ:
///
/// 1. ДВОЙНОЙ OverlapSphere УБРАН:
///    БЫЛО: DamageEnemiesInPuddle() + UpdateDebuffs() — два Physics.OverlapSphere
///          на каждый тик (причём UpdateDebuffs вызывался КАЖДЫЙ КАДР!)
///    СТАЛО: один Physics.OverlapSphereNonAlloc раз в TICK_RATE секунд.
///           Урон и обновление дебаффа делаются за один проход по результатам.
///
/// 2. new HashSet() УБРАН из горячего пути:
///    БЫЛО: var currentInPuddle = new HashSet&lt;EnemyHealth&gt;()  ← каждый кадр!
///    СТАЛО: _currentInPuddle — поле класса, очищается через Clear().
///
/// 3. Physics.OverlapSphereNonAlloc:
///    БЫЛО: Physics.OverlapSphere → new Collider[] каждый тик
///    СТАЛО: Physics.OverlapSphereNonAlloc с предаллоцированным _collidersBuffer.
///
/// 4. static RemoveCooldownForEnemy(EnemyHealth):
///    EnemyHealth.ResetState() вызывает этот метод при возврате в пул.
///    Переиспользованный враг не будет иметь старый кулдаун от предыдущей жизни.
///
/// ЖИЗНЕННЫЙ ЦИКЛ:
///   1. Init() — задаём damage, radius, duration, owner
///   2. OverlapSphereNonAlloc каждые TICK_RATE сек — хит по врагам + обновление дебаффа
///   3. По истечении duration — CleanupDebuffs() → Destroy
/// </summary>
public class AcidPuddle : MonoBehaviour
{
    public const float TICK_RATE  = 0.5f;     // секунд между тиками урона
    private const float FLAT_HEIGHT = 0.05f;  // фиксированная высота лужи (плоская)

    // Максимальное число коллайдеров за один OverlapSphere.
    // 100 достаточно даже при максимальной плотности врагов (700 всего, радиус лужи ~3 ед.)
    private const int MAX_COLLIDERS = 100;

    // ─── RUNTIME ДАННЫЕ ───────────────────────────────────────────────────────
    private float        _damage;
    private float        _radius;
    private float        _duration;
    private PlayerStats  _owner;
    private CombatCalculator _combatCalc;

    private float _tickTimer = 0f;
    private float _lifetime  = 0f;

    // ─── КЭШИРОВАННЫЕ СТРУКТУРЫ (без аллокаций в горячем пути) ──────────────
    // ИСПРАВЛЕНИЕ: HashSet — поле, не new каждый кадр.
    private readonly HashSet<EnemyHealth> _enemiesInPuddle  = new HashSet<EnemyHealth>();
    private readonly HashSet<EnemyHealth> _currentInPuddle  = new HashSet<EnemyHealth>();

    // ИСПРАВЛЕНИЕ: буфер для NonAlloc — один раз на весь объект.
    private readonly Collider[] _collidersBuffer = new Collider[MAX_COLLIDERS];

    // ─── ГЛОБАЛЬНЫЙ КУЛДАУН ХИТОВ (анти-стак) ────────────────────────────────
    // Статический — общий для ВСЕХ луж.
    // Не даёт одному врагу получить урон от нескольких луж за один тик.
    private static readonly Dictionary<EnemyHealth, float> _globalHitCooldowns
        = new Dictionary<EnemyHealth, float>(128);

    // Список для очистки устаревших записей — статический, без аллокации в loop.
    private static readonly List<EnemyHealth> _toRemove = new List<EnemyHealth>(32);

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public void Init(float damage, float radius, float duration,
                     PlayerStats owner, CombatCalculator combatCalc)
    {
        _damage     = damage;
        _radius     = radius;
        _duration   = duration;
        _owner      = owner;
        _combatCalc = combatCalc;
        _tickTimer  = 0f;  // первый тик сразу при спавне
        _lifetime   = 0f;

        // Плоский скейл — X/Z по радиусу, Y фиксирован
        transform.localScale = new Vector3(radius * 2f, FLAT_HEIGHT, radius * 2f);

        Debug.Log($"[AcidPuddle] Создана. DMG={damage:F1}, R={radius:F1}, Duration={duration:F1}s");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        _lifetime  += Time.deltaTime;
        _tickTimer += Time.deltaTime;

        // ИСПРАВЛЕНИЕ: один OverlapSphere вместо двух.
        // Урон И обновление дебаффа делаем за один физический запрос раз в TICK_RATE.
        if (_tickTimer >= TICK_RATE)
        {
            _tickTimer -= TICK_RATE;
            TickPuddle();
        }

        // Время вышло
        if (_lifetime >= _duration)
        {
            CleanupDebuffs();
            Destroy(gameObject);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОСНОВНОЙ ТИК — УРОН + ДЕБАФФ (единый проход)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Выполняет единый OverlapSphereNonAlloc — без аллокаций Collider[].
    /// За один проход: наносит урон (анти-стак) + обновляет дебаффы (enter/exit).
    /// </summary>
    private void TickPuddle()
    {
        // ИСПРАВЛЕНИЕ: NonAlloc — результаты пишутся в заранее выделенный буфер.
        var physScene = gameObject.scene.GetPhysicsScene();
        int hitCount = physScene.OverlapSphere(transform.position, _radius, _collidersBuffer, ~0, QueryTriggerInteraction.Collide);
        float now = Time.time;

        _currentInPuddle.Clear();

        for (int i = 0; i < hitCount; i++)
        {
            EnemyHealth enemy = _collidersBuffer[i].GetComponent<EnemyHealth>();
            if (enemy == null || !enemy.gameObject.activeInHierarchy) continue;

            _currentInPuddle.Add(enemy);

            // ── УРОН (анти-стак через глобальный кулдаун) ──────────────────
            if (_globalHitCooldowns.TryGetValue(enemy, out float lastHit))
            {
                if (now - lastHit < TICK_RATE)
                    goto ProcessDebuff; // пропускаем урон, но обновляем дебафф
            }

            _globalHitCooldowns[enemy] = now;
            float finalDamage = CalculateDamage(enemy);
            enemy.TakeDamage(finalDamage, _owner);

            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[AcidPuddle] Тик по {enemy.name}, DMG={finalDamage:F1}");
            #endif

            ProcessDebuff:

            // ── ДЕБАФФ ENTER: враг только что вошёл ──────────────────────────
            if (!_enemiesInPuddle.Contains(enemy))
                EnsureDebuffComponent(enemy).AddPuddle();
        }

        // ── ДЕБАФФ EXIT: враги вышли из лужи ─────────────────────────────────
        foreach (var enemy in _enemiesInPuddle)
        {
            if (!_currentInPuddle.Contains(enemy) && enemy != null)
            {
                var debuff = enemy.GetComponent<AcidDebuffComponent>();
                debuff?.RemovePuddle();
            }
        }

        // Синхронизируем _enemiesInPuddle ← _currentInPuddle
        _enemiesInPuddle.Clear();
        foreach (var e in _currentInPuddle)
            _enemiesInPuddle.Add(e);

        // Чистим устаревшие записи глобального кулдауна
        CleanStaleGlobalCooldowns(now);
    }

    private float CalculateDamage(EnemyHealth enemy)
    {
        if (_combatCalc != null && _owner != null)
        {
            var result = _combatCalc.Calculate(_damage, _owner, enemy);
            return result.FinalDamage;
        }
        return Mathf.Max(1f, _damage);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ДЕБАФФ: ВСПОМОГАТЕЛЬНЫЕ
    // ─────────────────────────────────────────────────────────────────────────

    private AcidDebuffComponent EnsureDebuffComponent(EnemyHealth enemy)
    {
        var debuff = enemy.GetComponent<AcidDebuffComponent>();
        if (debuff == null)
            debuff = enemy.gameObject.AddComponent<AcidDebuffComponent>();
        debuff.enabled = true;
        return debuff;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОЧИСТКА ПРИ УНИЧТОЖЕНИИ
    // ─────────────────────────────────────────────────────────────────────────

    private void CleanupDebuffs()
    {
        foreach (var enemy in _enemiesInPuddle)
        {
            if (enemy == null) continue;
            var debuff = enemy.GetComponent<AcidDebuffComponent>();
            debuff?.RemovePuddle();
        }
        _enemiesInPuddle.Clear();
    }

    void OnDestroy()
    {
        CleanupDebuffs();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СТАТИЧЕСКОЕ API ДЛЯ ВНЕШНИХ КЛАССОВ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Убирает врага из глобального кулдауна хитов.
    ///
    /// Вызывается из EnemyHealth.ResetState() при возврате врага в пул.
    /// Без этого переиспользованный враг мог быть невосприимчив к кислоте
    /// в течение TICK_RATE секунд после повторного появления:
    ///   пул-объект остаётся живым → pair.Key == null = false →
    ///   старая запись кулдауна применяется к новой "жизни" врага.
    /// </summary>
    public static void RemoveCooldownForEnemy(EnemyHealth enemy)
    {
        if (enemy != null)
            _globalHitCooldowns.Remove(enemy);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private static void CleanStaleGlobalCooldowns(float now)
    {
        // Запись устарела если прошло > 2 секунды (4 тика) или враг == null
        const float STALE_THRESHOLD = 2f;
        _toRemove.Clear();

        foreach (var pair in _globalHitCooldowns)
        {
            if (pair.Key == null || now - pair.Value > STALE_THRESHOLD)
                _toRemove.Add(pair.Key);
        }

        foreach (var key in _toRemove)
            _globalHitCooldowns.Remove(key);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 0.1f, 0.35f);
        Gizmos.DrawSphere(transform.position, _radius);
        Gizmos.color = new Color(0.2f, 0.8f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, _radius);
    }
#endif
}
