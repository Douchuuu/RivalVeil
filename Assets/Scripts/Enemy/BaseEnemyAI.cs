using UnityEngine;
using VContainer;

/// <summary>
/// Настройки взбирания на препятствия — задаются отдельно для каждого типа врага.
/// Мини-босс с x2 коллайдером требует увеличенных значений.
/// </summary>
[System.Serializable]
public class EnemyClimbSettings
{
    [Tooltip("Высота проверки над коллайдером — нет препятствия выше = можно перевалить.\n" +
             "Обычный моб: ~1.4  |  МиниБосс (x2): ~2.6+")]
    public float stepCheckHeight = 1.4f;

    [Tooltip("Вертикальная скорость при карабкании")]
    public float stepUpForce = 10f;

    [Tooltip("Ускорение вперёд при перевале (Vault)")]
    public float vaultForwardBoost = 2.0f;
}

/// <summary>
/// BaseEnemyAI — абстрактный базовый класс для ВСЕХ типов врагов.
///
/// ═══════════════════════════════════════════════════════════════════
/// АРХИТЕКТУРА
/// ═══════════════════════════════════════════════════════════════════
///
///   Все типы врагов наследуют BaseEnemyAI:
///     EnemyAI    : BaseEnemyAI   — обычный моб
///     MiniBossAI : BaseEnemyAI   — мини-босс
///     [Новый]    : BaseEnemyAI   — новый тип
///
///   EnemyBatchSystem работает с BaseEnemyAI — не нужно трогать
///   BatchSystem при добавлении нового типа моба.
///
/// ═══════════════════════════════════════════════════════════════════
/// ЧТО ЗДЕСЬ, А ЧТО В НАСЛЕДНИКАХ
/// ═══════════════════════════════════════════════════════════════════
///
///   BaseEnemyAI содержит:
///     - Stats: speed, damage, attackCooldown (Inspector-поля)
///     - Rigidbody setup (Awake)
///     - Регистрация/дерегистрация в EnemyBatchSystem (OnEnable/OnDisable)
///     - IsStunned, Stun()
///     - SetDamage/SetSpeed/GetDamage/GetSpeed/ResetToBase
///     - _isMiniBoss флаг (auto-detect через GetComponent в Awake)
///
///   Наследники реализуют:
///     - OnAttackPlayer() — логика атаки (урон, knockback, спецэффекты)
///     - IsPlayerOnTop()  — опционально, если нужна stomp-логика
///     - Свою специфику (фазы, ауры, дэши — всё угодно)
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(EnemyHealth))]
public abstract class BaseEnemyAI : MonoBehaviour
{
    [Header("Характеристики")]
    public float speed = 3.5f;
    public float damage = 10f;
    public float attackCooldown = 1f;

    [Header("Взбирание на препятствия")]
    [Tooltip("Настройки взбирания. МиниБосс: stepCheckHeight ~2.6, stepUpForce ~14")]
    public EnemyClimbSettings climbSettings = new EnemyClimbSettings();

    // ── Базовые значения (восстанавливаются при возврате в пул) ──────────────
    private float _baseDamage;
    private float _baseSpeed;

    // ── Стан ──────────────────────────────────────────────────────────────────
    protected float _stunUntil = 0f;
    public bool IsStunned => Time.time < _stunUntil;

    // ── Таймер атаки ──────────────────────────────────────────────────────────
    protected float _nextAttackTime;

    // ── Компоненты ────────────────────────────────────────────────────────────
    protected Rigidbody _rb;
    protected EnemyHealth _enemyHealth;
    private int _batchIndex = -1;
    private bool _isMiniBoss;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────
    protected GameStateService _gameState;
    protected EnemyBatchSystem _batchSystem;

    [Inject]
    public virtual void Construct(GameStateService gameState, EnemyBatchSystem batchSystem)
    {
        _gameState = gameState;
        _batchSystem = batchSystem;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _enemyHealth = GetComponent<EnemyHealth>();
        _isMiniBoss = this is MiniBossAI;

        _baseDamage = damage;
        _baseSpeed = speed;

        if (_rb != null)
        {
            _rb.isKinematic = false;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rb.constraints = RigidbodyConstraints.FreezeRotation;
            _rb.useGravity = true;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0f;
        }
        else
        {
            Debug.LogWarning($"[{GetType().Name}] {name}: Rigidbody не найден!");
        }
    }

    protected virtual void OnEnable()
    {
        if (_rb != null) _rb.linearVelocity = Vector3.zero;

        _nextAttackTime = 0f;
        _stunUntil = 0f;
        _batchIndex = -1;

        if (_batchSystem != null && _rb != null)
            _batchIndex = _batchSystem.Register(this, _rb, speed, _isMiniBoss, climbSettings);
    }

    protected virtual void OnDisable()
    {
        if (_rb != null) _rb.linearVelocity = Vector3.zero;

        if (_batchSystem != null && _batchIndex >= 0)
        {
            _batchSystem.Unregister(this);
            _batchIndex = -1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АТАКА — коллизии обрабатываются здесь, логика атаки — в наследнике
    // ─────────────────────────────────────────────────────────────────────────

    private void OnCollisionEnter(Collision collision) => TryAttack(collision);
    private void OnCollisionStay(Collision collision) => TryAttack(collision);

    private void TryAttack(Collision collision)
    {
        if (!collision.gameObject.CompareTag("Player")) return;
        if (Time.time < _nextAttackTime) return;
        if (_gameState != null && _gameState.IsPaused) return;

        _nextAttackTime = Time.time + attackCooldown;
        OnAttackPlayer(collision);
    }

    /// <summary>
    /// Логика атаки — реализуется в каждом наследнике по-своему.
    /// Вызывается только когда кулдаун прошёл и игра не на паузе.
    /// </summary>
    protected abstract void OnAttackPlayer(Collision collision);

    // ─────────────────────────────────────────────────────────────────────────
    // СТАН
    // ─────────────────────────────────────────────────────────────────────────

    public void Stun(float duration)
    {
        _stunUntil = Time.time + duration;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ГЕТТЕРЫ / СЕТТЕРЫ / СБРОС
    // ─────────────────────────────────────────────────────────────────────────

    public void SetDamage(float newDamage) => damage = newDamage;

    public void SetSpeed(float newSpeed)
    {
        speed = newSpeed;
        if (_batchSystem != null && _batchIndex >= 0)
            _batchSystem.UpdateSpeed(_batchIndex, newSpeed);
    }

    public float GetDamage() => damage;
    public float GetSpeed() => speed;

    /// <summary>
    /// ФИКС: Обновляет _batchIndex после swap-and-pop в EnemyBatchSystem.Unregister().
    ///
    /// Проблема: при Unregister() последний враг в массиве перемещается на место
    /// удалённого. _indexMap обновляется корректно, но _batchIndex внутри
    /// BaseEnemyAI остаётся устаревшим (хранит старый индекс 'last').
    ///
    /// Последствие: SetSpeed() вызывает UpdateSpeed(_batchIndex=old, ...)
    /// → индекс >= _count → обновление молча игнорируется → враг движется
    /// со старой скоростью независимо от ApplyDifficultyToEnemy.
    ///
    /// Вызывается ТОЛЬКО из EnemyBatchSystem.Unregister() после swap.
    /// </summary>
    internal void UpdateBatchIndex(int newIndex) => _batchIndex = newIndex;

    public virtual void ResetToBase()
    {
        damage = _baseDamage;
        speed = _baseSpeed;
        _nextAttackTime = 0f;
        _stunUntil = 0f;
    }
}