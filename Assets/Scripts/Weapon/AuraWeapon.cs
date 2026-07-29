using UnityEngine;
using Unity.Netcode;
using VContainer;

/// <summary>
/// AuraWeapon v6 — убран legacy IUpgradeable.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
/// Вызывается из LevelUpManager → WeaponUpgradeOffer.Apply()
///
/// Поддерживаемые статы:
///   Damage   — % к базовому урону за тик
///   Radius   — % к радиусу ауры (масштабирует и визуальное кольцо)
///   TickRate — % к частоте тиков (уменьшает интервал)
///
/// Глобальный AttackSpeed из StatSheet применяется в Update() — не хранится
/// в оружии. Это позволяет бонусам от предметов работать без пересчёта ауры.
///
/// ══════════════════════════════════════════════════════════════
/// ВИЗУАЛ:
/// 1. КОЛЬЦО НА ПОЛУ (auraRingObject) — масштабируется по радиусу
/// 2. ПОСТОЯННЫЕ ЧАСТИЦЫ (auraAmbientEffect) — loop=true пока аура надета
/// 3. ПУЛЬС ПРИ ТИКЕ (auraPulseEffect) — one-shot при каждом тике урона
/// 4. ХИТ-ЭФФЕКТ НА ВРАГЕ (hitEffectPrefab)
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class AuraWeapon : MonoBehaviour, IDynamicUpgradeable
{
    [Header("🌟 ХАРАКТЕРИСТИКИ АУРЫ")]
    [SerializeField] private float baseDamage   = 5f;
    [SerializeField] private float baseRadius   = 5f;
    [SerializeField] private float baseTickRate = 0.5f;

    [Header("🔵 КОЛЬЦО НА ПОЛУ")]
    [Tooltip("Визуальное кольцо/декаль — масштабируется по радиусу ауры автоматически.")]
    [SerializeField] private GameObject auraRingObject;
    [SerializeField] private bool  auraRingIsPrefab         = false;
    [SerializeField] private float auraRingScaleMultiplier  = 1.0f;
    [SerializeField] private float auraRingYScale           = 0.02f;
    [Range(0f, 1f)]
    [SerializeField] private float auraRingOpacity          = 0.5f;
    [SerializeField] private float auraRingGroundOffset     = 0.05f;
    [SerializeField] private float playerCapsuleHalfHeight  = 1.0f;

    [Header("✨ ПОСТОЯННЫЕ ЧАСТИЦЫ")]
    [SerializeField] private GameObject auraAmbientEffect;
    [SerializeField] private bool auraAmbientIsPrefab = false;

    [Header("💫 ПУЛЬС ПРИ ТИКЕ")]
    [SerializeField] private GameObject auraPulseEffect;
    [SerializeField] private bool  auraPulseIsPrefab  = true;
    [SerializeField] private float auraPulseDuration  = 0.35f;

    [Header("💥 ХИТ-ЭФФЕКТ НА ВРАГЕ")]
    [SerializeField] private AuraHitMode auraHitMode = AuraHitMode.OriginPulse;
    [SerializeField] private GameObject hitEffectPrefab;
    [SerializeField] private float hitEffectDuration = 0.4f;

    // ── Текущие значения (только апгрейды ауры) ──────────────────────────────
    // Буфер для PhysicsScene.OverlapSphere
    private readonly Collider[] _auraBuffer = new Collider[128];

    private float _currentDamage;
    private float _currentRadius;
    private float _currentTickRate;  // только апгрейды ауры, без глобального StatSheet

    // ── Накопленные бонусы от апгрейдов ─────────────────────────────────────
    private float _bonusDamagePct    = 0f;
    private float _bonusRadiusPct    = 0f;
    private float _bonusTickRatePct  = 0f;

    private float _tickTimer;
    private NetworkBehaviour _playerNetwork;
    private PlayerStats      _playerStats;
    private CombatCalculator _combatCalc;

    private GameObject     _spawnedRing;
    private Transform      _ringTransform;
    private GameObject     _spawnedAmbient;
    private ParticleSystem _ambientPS;
    private ParticleSystem _pulsePS;

    [Inject]
    public void Construct(CombatCalculator combatCalc) => _combatCalc = combatCalc;

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    void Awake() => RecalcStats();

    void Start()
    {
        _playerNetwork = GetComponentInParent<NetworkBehaviour>();
        _playerStats   = GetComponentInParent<PlayerStats>();

        StartAuraRing();
        StartAmbientEffect();
        CachePulseEffect();
    }

    void OnDestroy()
    {
        StopAuraRing();
        StopAmbientEffect();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — КОЛЬЦО
    // ─────────────────────────────────────────────────────────────────────────

    private void StartAuraRing()
    {
        if (auraRingObject == null) return;

        if (auraRingIsPrefab)
        {
            _spawnedRing   = Instantiate(auraRingObject);
            _ringTransform = _spawnedRing.transform;
        }
        else
        {
            auraRingObject.SetActive(true);
            _ringTransform = auraRingObject.transform;
        }

        SnapRingToFeet();
        ApplyRingOpacity(_ringTransform?.gameObject);
        UpdateRingScale();
    }

    private void StopAuraRing()
    {
        if (auraRingIsPrefab) { if (_spawnedRing != null) Destroy(_spawnedRing); }
        else auraRingObject?.SetActive(false);
    }

    private void UpdateRingScale()
    {
        GameObject ring = auraRingIsPrefab ? _spawnedRing : auraRingObject;
        if (ring == null) return;

        float diameter = _currentRadius * 2f * auraRingScaleMultiplier;
        ring.transform.localScale = new Vector3(diameter, auraRingYScale, diameter);
    }

    private void ApplyRingOpacity(GameObject ring)
    {
        if (ring == null) return;
        Renderer rend = ring.GetComponent<Renderer>();
        if (rend == null) return;

        Material mat = rend.material;
        Color c      = mat.color;
        c.a          = auraRingOpacity;
        mat.color    = c;
    }

    private void SnapRingToFeet()
    {
        if (_ringTransform == null) return;
        Vector3 feetPos = transform.position
                          + Vector3.down  * playerCapsuleHalfHeight
                          + Vector3.up    * auraRingGroundOffset;
        _ringTransform.position = feetPos;
        _ringTransform.rotation = Quaternion.identity;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — AMBIENT
    // ─────────────────────────────────────────────────────────────────────────

    private void StartAmbientEffect()
    {
        if (auraAmbientEffect == null) return;

        GameObject ambObj;
        if (auraAmbientIsPrefab)
        {
            _spawnedAmbient = Instantiate(auraAmbientEffect, transform);
            _spawnedAmbient.transform.localPosition = Vector3.zero;
            ambObj = _spawnedAmbient;
        }
        else
        {
            ambObj = auraAmbientEffect;
            ambObj.SetActive(true);
        }

        foreach (var ps in ambObj.GetComponentsInChildren<ParticleSystem>(true))
            if (!ps.isPlaying) ps.Play();

        _ambientPS = ambObj.GetComponent<ParticleSystem>();
    }

    private void StopAmbientEffect()
    {
        if (auraAmbientIsPrefab) { if (_spawnedAmbient != null) Destroy(_spawnedAmbient); }
        else if (auraAmbientEffect != null)
            foreach (var ps in auraAmbientEffect.GetComponentsInChildren<ParticleSystem>())
                ps.Stop();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — ПУЛЬС
    // ─────────────────────────────────────────────────────────────────────────

    private void CachePulseEffect()
    {
        if (auraPulseEffect == null || auraPulseIsPrefab) return;
        _pulsePS = auraPulseEffect.GetComponent<ParticleSystem>();
    }

    private void PlayPulse()
    {
        if (auraPulseEffect == null) return;

        if (auraPulseIsPrefab)
        {
            GameObject pulse = Instantiate(auraPulseEffect, transform.position, Quaternion.identity);
            Destroy(pulse, auraPulseDuration);
        }
        else if (_pulsePS != null)
        {
            _pulsePS.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _pulsePS.Play();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ХИТ-ЭФФЕКТ
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnHitEffect(Vector3 position)
    {
        if (hitEffectPrefab == null) return;
        GameObject fx = Instantiate(hitEffectPrefab, position + Vector3.up * 0.3f, Quaternion.identity);
        Destroy(fx, hitEffectDuration);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЦИКЛ АТАКИ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsLocalPlayer()) return;
        if (_playerStats?.IsAttackBlocked == true) return;

        _tickTimer -= Time.deltaTime;
        if (_tickTimer <= 0f)
        {
            DealAuraDamage();

            // _currentTickRate = только апгрейды ауры
            // globalSpeedMult  = базовый attackSpeed персонажа + предметы (Факел и т.д.)
            float globalSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.AttackSpeed) ?? 1f;
            _tickTimer = _currentTickRate / globalSpeedMult;
        }
    }

    void LateUpdate()
    {
        if (_ringTransform != null) SnapRingToFeet();
    }

    void DealAuraDamage()
    {
        var physScene = gameObject.scene.GetPhysicsScene();

        // AttackRadiusMultiplier — глобальный множитель радиуса персонажа из StatSheet.
        // Аура радиус = weapon.baseRadius × char.AttackRadiusMultiplier × (1 + upgrade%)
        float charRadiusMult  = _playerStats?.StatSheet?.GetStat(StatType.AttackRadiusMultiplier) ?? 1f;
        float effectiveRadius = _currentRadius * charRadiusMult;

        int colCount = physScene.OverlapSphere(transform.position, effectiveRadius, _auraBuffer, ~0, QueryTriggerInteraction.Collide);
        Collider[] cols = new Collider[colCount];
        System.Array.Copy(_auraBuffer, cols, colCount);
        int  hitCount            = 0;
        bool originPulseSpawned  = false;

        foreach (var col in cols)
        {
            EnemyHealth enemy = col.GetComponent<EnemyHealth>();
            if (enemy == null) continue;

            enemy.TakeDamage(CalculateDamage(enemy), _playerStats);
            hitCount++;

            switch (auraHitMode)
            {
                case AuraHitMode.EnemyFlash:
                    SpawnHitEffect(col.transform.position);
                    break;
                case AuraHitMode.OriginPulse when !originPulseSpawned:
                    SpawnHitEffect(transform.position);
                    originPulseSpawned = true;
                    break;
            }
        }

        if (hitCount > 0) PlayPulse();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (hitCount > 0)
            Debug.Log($"🌟 Aura tick! Врагов={hitCount}, DMG={_currentDamage:F1}");
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАСЧЁТ УРОНА
    // ─────────────────────────────────────────────────────────────────────────

    float CalculateDamage(EnemyHealth enemy = null)
    {
        if (_combatCalc != null && _playerStats != null)
            return _combatCalc.Calculate(_currentDamage, _playerStats, enemy).FinalDamage;
        return _currentDamage;
    }

    void RecalcStats()
    {
        _currentDamage   = baseDamage   * (1f + _bonusDamagePct   / 100f);
        _currentRadius   = baseRadius   * (1f + _bonusRadiusPct   / 100f);
        _currentTickRate = baseTickRate / (1f + _bonusTickRatePct / 100f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.Damage:   return _currentDamage;
            case StatBonusType.Radius:   return _currentRadius;
            case StatBonusType.TickRate: return _currentTickRate;
            default:                     return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.Damage,
        StatBonusType.Radius,
        StatBonusType.TickRate,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        bool radiusChanged = false;
        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.Damage:   _bonusDamagePct   += bonus.value; break;
                case StatBonusType.Radius:   _bonusRadiusPct   += bonus.value; radiusChanged = true; break;
                case StatBonusType.TickRate: _bonusTickRatePct += bonus.value; break;
            }
        }
        RecalcStats();
        if (radiusChanged) UpdateRingScale();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private bool IsLocalPlayer()
    {
        // Dedicated Server не является локальным игроком
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsHost) return false;
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    public float GetDamage()    => _currentDamage;
    public float GetRadius()    => _currentRadius;
    public float GetTickRate()  => _currentTickRate;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, _currentRadius);
    }
#endif
}

// ─────────────────────────────────────────────────────────────────────────────

public enum AuraHitMode
{
    OriginPulse,  // один эффект в центре — лучше для производительности
    EnemyFlash,   // эффект на каждом враге — нагляднее
}
/*using UnityEngine;
using Unity.Netcode;
using VContainer;

/// <summary>
/// AuraWeapon v6 — убран legacy IUpgradeable.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
/// Вызывается из LevelUpManager → WeaponUpgradeOffer.Apply()
///
/// Поддерживаемые статы:
///   Damage   — % к базовому урону за тик
///   Radius   — % к радиусу ауры (масштабирует и визуальное кольцо)
///   TickRate — % к частоте тиков (уменьшает интервал)
///
/// Глобальный AttackSpeed из StatSheet применяется в Update() — не хранится
/// в оружии. Это позволяет бонусам от предметов работать без пересчёта ауры.
///
/// ══════════════════════════════════════════════════════════════
/// ВИЗУАЛ:
/// 1. КОЛЬЦО НА ПОЛУ (auraRingObject) — масштабируется по радиусу
/// 2. ПОСТОЯННЫЕ ЧАСТИЦЫ (auraAmbientEffect) — loop=true пока аура надета
/// 3. ПУЛЬС ПРИ ТИКЕ (auraPulseEffect) — one-shot при каждом тике урона
/// 4. ХИТ-ЭФФЕКТ НА ВРАГЕ (hitEffectPrefab)
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class AuraWeapon : MonoBehaviour, IDynamicUpgradeable
{
    [Header("🌟 ХАРАКТЕРИСТИКИ АУРЫ")]
    [SerializeField] private float baseDamage   = 5f;
    [SerializeField] private float baseRadius   = 5f;
    [SerializeField] private float baseTickRate = 0.5f;

    [Header("🔵 КОЛЬЦО НА ПОЛУ")]
    [Tooltip("Визуальное кольцо/декаль — масштабируется по радиусу ауры автоматически.")]
    [SerializeField] private GameObject auraRingObject;
    [SerializeField] private bool  auraRingIsPrefab         = false;
    [SerializeField] private float auraRingScaleMultiplier  = 1.0f;
    [SerializeField] private float auraRingYScale           = 0.02f;
    [Range(0f, 1f)]
    [SerializeField] private float auraRingOpacity          = 0.5f;
    [SerializeField] private float auraRingGroundOffset     = 0.05f;
    [SerializeField] private float playerCapsuleHalfHeight  = 1.0f;

    [Header("✨ ПОСТОЯННЫЕ ЧАСТИЦЫ")]
    [SerializeField] private GameObject auraAmbientEffect;
    [SerializeField] private bool auraAmbientIsPrefab = false;

    [Header("💫 ПУЛЬС ПРИ ТИКЕ")]
    [SerializeField] private GameObject auraPulseEffect;
    [SerializeField] private bool  auraPulseIsPrefab  = true;
    [SerializeField] private float auraPulseDuration  = 0.35f;

    [Header("💥 ХИТ-ЭФФЕКТ НА ВРАГЕ")]
    [SerializeField] private AuraHitMode auraHitMode = AuraHitMode.OriginPulse;
    [SerializeField] private GameObject hitEffectPrefab;
    [SerializeField] private float hitEffectDuration = 0.4f;

    // ── Текущие значения (только апгрейды ауры) ──────────────────────────────
    // Буфер для PhysicsScene.OverlapSphere
    private readonly Collider[] _auraBuffer = new Collider[128];

    private float _currentDamage;
    private float _currentRadius;
    private float _currentTickRate;  // только апгрейды ауры, без глобального StatSheet

    // ── Накопленные бонусы от апгрейдов ─────────────────────────────────────
    private float _bonusDamagePct    = 0f;
    private float _bonusRadiusPct    = 0f;
    private float _bonusTickRatePct  = 0f;

    private float _tickTimer;
    private NetworkBehaviour _playerNetwork;
    private PlayerStats      _playerStats;
    private CombatCalculator _combatCalc;

    private GameObject     _spawnedRing;
    private Transform      _ringTransform;
    private GameObject     _spawnedAmbient;
    private ParticleSystem _ambientPS;
    private ParticleSystem _pulsePS;

    [Inject]
    public void Construct(CombatCalculator combatCalc) => _combatCalc = combatCalc;

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    void Awake() => RecalcStats();

    void Start()
    {
        _playerNetwork = GetComponentInParent<NetworkBehaviour>();
        _playerStats   = GetComponentInParent<PlayerStats>();

        StartAuraRing();
        StartAmbientEffect();
        CachePulseEffect();
    }

    void OnDestroy()
    {
        StopAuraRing();
        StopAmbientEffect();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — КОЛЬЦО
    // ─────────────────────────────────────────────────────────────────────────

    private void StartAuraRing()
    {
        if (auraRingObject == null) return;

        if (auraRingIsPrefab)
        {
            _spawnedRing   = Instantiate(auraRingObject);
            _ringTransform = _spawnedRing.transform;
        }
        else
        {
            auraRingObject.SetActive(true);
            _ringTransform = auraRingObject.transform;
        }

        SnapRingToFeet();
        ApplyRingOpacity(_ringTransform?.gameObject);
        UpdateRingScale();
    }

    private void StopAuraRing()
    {
        if (auraRingIsPrefab) { if (_spawnedRing != null) Destroy(_spawnedRing); }
        else auraRingObject?.SetActive(false);
    }

    private void UpdateRingScale()
    {
        GameObject ring = auraRingIsPrefab ? _spawnedRing : auraRingObject;
        if (ring == null) return;

        float diameter = _currentRadius * 2f * auraRingScaleMultiplier;
        ring.transform.localScale = new Vector3(diameter, auraRingYScale, diameter);
    }

    private void ApplyRingOpacity(GameObject ring)
    {
        if (ring == null) return;
        Renderer rend = ring.GetComponent<Renderer>();
        if (rend == null) return;

        Material mat = rend.material;
        Color c      = mat.color;
        c.a          = auraRingOpacity;
        mat.color    = c;
    }

    private void SnapRingToFeet()
    {
        if (_ringTransform == null) return;
        Vector3 feetPos = transform.position
                          + Vector3.down  * playerCapsuleHalfHeight
                          + Vector3.up    * auraRingGroundOffset;
        _ringTransform.position = feetPos;
        _ringTransform.rotation = Quaternion.identity;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — AMBIENT
    // ─────────────────────────────────────────────────────────────────────────

    private void StartAmbientEffect()
    {
        if (auraAmbientEffect == null) return;

        GameObject ambObj;
        if (auraAmbientIsPrefab)
        {
            _spawnedAmbient = Instantiate(auraAmbientEffect, transform);
            _spawnedAmbient.transform.localPosition = Vector3.zero;
            ambObj = _spawnedAmbient;
        }
        else
        {
            ambObj = auraAmbientEffect;
            ambObj.SetActive(true);
        }

        foreach (var ps in ambObj.GetComponentsInChildren<ParticleSystem>(true))
            if (!ps.isPlaying) ps.Play();

        _ambientPS = ambObj.GetComponent<ParticleSystem>();
    }

    private void StopAmbientEffect()
    {
        if (auraAmbientIsPrefab) { if (_spawnedAmbient != null) Destroy(_spawnedAmbient); }
        else if (auraAmbientEffect != null)
            foreach (var ps in auraAmbientEffect.GetComponentsInChildren<ParticleSystem>())
                ps.Stop();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ — ПУЛЬС
    // ─────────────────────────────────────────────────────────────────────────

    private void CachePulseEffect()
    {
        if (auraPulseEffect == null || auraPulseIsPrefab) return;
        _pulsePS = auraPulseEffect.GetComponent<ParticleSystem>();
    }

    private void PlayPulse()
    {
        if (auraPulseEffect == null) return;

        if (auraPulseIsPrefab)
        {
            GameObject pulse = Instantiate(auraPulseEffect, transform.position, Quaternion.identity);
            Destroy(pulse, auraPulseDuration);
        }
        else if (_pulsePS != null)
        {
            _pulsePS.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _pulsePS.Play();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ХИТ-ЭФФЕКТ
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnHitEffect(Vector3 position)
    {
        if (hitEffectPrefab == null) return;
        GameObject fx = Instantiate(hitEffectPrefab, position + Vector3.up * 0.3f, Quaternion.identity);
        Destroy(fx, hitEffectDuration);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЦИКЛ АТАКИ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsLocalPlayer()) return;
        if (_playerStats?.IsAttackBlocked == true) return;

        _tickTimer -= Time.deltaTime;
        if (_tickTimer <= 0f)
        {
            DealAuraDamage();

            // _currentTickRate = только апгрейды ауры
            // globalSpeedMult  = базовый attackSpeed персонажа + предметы (Факел и т.д.)
            float globalSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.AttackSpeed) ?? 1f;
            _tickTimer = _currentTickRate / globalSpeedMult;
        }
    }

    void LateUpdate()
    {
        if (_ringTransform != null) SnapRingToFeet();
    }

    void DealAuraDamage()
    {
        var physScene = gameObject.scene.GetPhysicsScene();

        // AttackRadiusMultiplier — глобальный множитель радиуса персонажа из StatSheet.
        // Аура радиус = weapon.baseRadius × char.AttackRadiusMultiplier × (1 + upgrade%)
        float charRadiusMult  = _playerStats?.StatSheet?.GetStat(StatType.AttackRadiusMultiplier) ?? 1f;
        float effectiveRadius = _currentRadius * charRadiusMult;

        int colCount = physScene.OverlapSphere(transform.position, effectiveRadius, _auraBuffer, ~0, QueryTriggerInteraction.Collide);
        Collider[] cols = new Collider[colCount];
        System.Array.Copy(_auraBuffer, cols, colCount);
        int  hitCount            = 0;
        bool originPulseSpawned  = false;

        foreach (var col in cols)
        {
            EnemyHealth enemy = col.GetComponent<EnemyHealth>();
            if (enemy == null) continue;

            enemy.TakeDamage(CalculateDamage(enemy), _playerStats);
            hitCount++;

            switch (auraHitMode)
            {
                case AuraHitMode.EnemyFlash:
                    SpawnHitEffect(col.transform.position);
                    break;
                case AuraHitMode.OriginPulse when !originPulseSpawned:
                    SpawnHitEffect(transform.position);
                    originPulseSpawned = true;
                    break;
            }
        }

        if (hitCount > 0) PlayPulse();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (hitCount > 0)
            Debug.Log($"🌟 Aura tick! Врагов={hitCount}, DMG={_currentDamage:F1}");
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАСЧЁТ УРОНА
    // ─────────────────────────────────────────────────────────────────────────

    float CalculateDamage(EnemyHealth enemy = null)
    {
        if (_combatCalc != null && _playerStats != null)
            return _combatCalc.Calculate(_currentDamage, _playerStats, enemy).FinalDamage;
        return _currentDamage;
    }

    void RecalcStats()
    {
        _currentDamage   = baseDamage   * (1f + _bonusDamagePct   / 100f);
        _currentRadius   = baseRadius   * (1f + _bonusRadiusPct   / 100f);
        _currentTickRate = baseTickRate / (1f + _bonusTickRatePct / 100f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.Damage:   return _currentDamage;
            case StatBonusType.Radius:   return _currentRadius;
            case StatBonusType.TickRate: return _currentTickRate;
            default:                     return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.Damage,
        StatBonusType.Radius,
        StatBonusType.TickRate,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        bool radiusChanged = false;
        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.Damage:   _bonusDamagePct   += bonus.value; break;
                case StatBonusType.Radius:   _bonusRadiusPct   += bonus.value; radiusChanged = true; break;
                case StatBonusType.TickRate: _bonusTickRatePct += bonus.value; break;
            }
        }
        RecalcStats();
        if (radiusChanged) UpdateRingScale();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private bool IsLocalPlayer()
    {
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    public float GetDamage()    => _currentDamage;
    public float GetRadius()    => _currentRadius;
    public float GetTickRate()  => _currentTickRate;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, _currentRadius);
    }
#endif
}

// ─────────────────────────────────────────────────────────────────────────────

public enum AuraHitMode
{
    OriginPulse,  // один эффект в центре — лучше для производительности
    EnemyFlash,   // эффект на каждом враге — нагляднее
}
*/