using UnityEngine;
using Unity.Netcode;
using VContainer;
using VContainer.Unity;

/// <summary>
/// PlayerMovement — движение игрока (Megabonk / bhop стиль).
///
/// ХАРАКТЕРИСТИКИ ИЗ STATSHEET:
///   MoveSpeedMultiplier  → умножается на walkSpeed и maxAirSpeed
///   ExtraJumps           → определяет maxJumpCount (0=1 прыжок, 1=2 прыжка...)
///   JumpForceMultiplier  → умножается на jumpForce (высота прыжка)
///   PickupRange          → радиус подбора XP-орбов
///
/// Формулы:
///   effectiveWalkSpeed = walkSpeed  * MoveSpeedMultiplier
///   effectiveAirSpeed  = maxAirSpeed * MoveSpeedMultiplier
///   effectiveJumpForce = jumpForce  * JumpForceMultiplier
///   maxJumps           = 1 + ExtraJumps  (1 = только с земли, 2 = земля+воздух)
/// </summary>
public class PlayerMovement : NetworkBehaviour
{
    [Header("Ссылки")]
    public Rigidbody rb;
    public Transform cam;
    public GameObject cameraPivotObject;

    [Header("Движение")]
    [Tooltip("Базовая скорость. Умножается на MoveSpeedMultiplier из StatSheet.")]
    public float walkSpeed = 10f;
    [Tooltip("Максимальная скорость в воздухе. Умножается на MoveSpeedMultiplier.")]
    public float maxAirSpeed = 26f;
    public float acceleration    = 80f;
    public float airAcceleration = 40f;
    public float jumpForce       = 9f;
    public float groundDrag      = 7f;
    public float airDrag         = 0.05f;

    [Header("Мультипрыжок (Inspector — дефолт без персонажа)")]
    [Tooltip("Используется как ЗАПАСНОЕ значение. StatSheet.ExtraJumps имеет приоритет.")]
    public int   maxJumpCount          = 2;
    public float airJumpForceMultiplier = 0.85f;

    [Header("Банихоп")]
    public float bhopGraceTime = 0.12f;

    [Header("Скольжение (CTRL)")]
    public float slideAngleMin       = 12f;
    public float slideForce          = 20f;
    public float slideDrag           = 0.5f;
    public float slideColliderHeight = 1.0f;

    [Header("Подбор орбов")]
    [Tooltip("Базовый радиус. Заменяется StatSheet.PickupRange если доступен.")]
    public float pickupRange = 5f;

    [Header("Ghost Prefab (Competitive)")]
    [SerializeField] private GameObject ghostPrefab;

    [Header("Knockback")]
    public float knockbackForce          = 8f;
    public float knockbackCooldown       = 0.2f;
    public float knockbackSlowDuration   = 1.2f;
    [Range(0.1f, 1f)]
    public float knockbackSpeedMultiplier = 0.35f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private float    _nextKnockbackTime;
    private float    _knockbackSlowUntil;
    private GhostSync _spawnedGhost  = null;
    private bool     _ghostSpawned   = false;

    private bool  _isGrounded    = false;
    private float _groundedTimer = 0f;
    private float _lastLandTime  = -999f;
    private int   _jumpsRemaining = 0;

    private bool      _isSliding       = false;
    private RaycastHit _slideGroundHit;
    private CapsuleCollider _capsule;
    private float     _defaultColHeight;

    private float _nextPickupCheckTime = 0f;
    private const float PICKUP_INTERVAL = 0.1f;

    private static readonly Collider[] _overlapBuffer = new Collider[64];
    private static int _orbLayerMask = -1;
    private static int OrbLayerMask
    {
        get
        {
            if (_orbLayerMask == -1)
            {
                int layer = LayerMask.NameToLayer("ExperienceOrb");
                _orbLayerMask = layer >= 0 ? (1 << layer) : ~0;
                if (layer < 0) Debug.LogWarning("[PlayerMovement] Слой 'ExperienceOrb' не найден!");
            }
            return _orbLayerMask;
        }
    }

    // ─── ЗАВИСИМОСТИ ──────────────────────────────────────────────────────────

    private GameStateService _gameState;
    private PlayerStats      _playerStats;

    [Inject]
    public void Construct(GameStateService gameState)
    {
        _gameState = gameState;
    }

    private new bool IsLocalPlayer()
    {
        if (IsServer && !IsHost) return false;
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return true;
        return IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NETWORK SPAWN
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        try { InjectionProvider.Container?.InjectGameObject(gameObject); }
        catch (System.Exception ex) { Debug.LogError($"[PlayerMovement] {ex.Message}"); }

        _playerStats = GetComponent<PlayerStats>();
        if (_playerStats == null)
            Debug.LogWarning("[PlayerMovement] PlayerStats не найден — MoveSpeed/Jump из StatSheet не будут работать.");

        if (IsOwner)
        {
            if (cameraPivotObject == null)
            {
                Transform foundPivot = transform.Find("CameraPivot");
                if (foundPivot != null) { cameraPivotObject = foundPivot.gameObject; cam = foundPivot; }
            }

            if (cameraPivotObject != null)
            {
                cameraPivotObject.SetActive(true);
                cam = cameraPivotObject.transform;
            }

            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule != null) _defaultColHeight = _capsule.height;
        }
        else
        {
            foreach (var r in GetComponentsInChildren<Renderer>())     r.enabled = false;
            foreach (var c in GetComponentsInChildren<Camera>(true))   c.gameObject.SetActive(false);

            if (cameraPivotObject != null) cameraPivotObject.SetActive(false);
            Transform pivot = transform.Find("CameraPivot");
            if (pivot != null) pivot.gameObject.SetActive(false);

            if (rb != null) { rb.isKinematic = true; rb.detectCollisions = false; }
            foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = false;
        }

        if (IsServer && GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;

        if (IsServer && GameModeManager.IsCompetitiveMode())
            SpawnGhost();
    }

    private void OnGameModeChanged(GameMode newMode)
    {
        if (!IsServer || _ghostSpawned) return;
        if (newMode == GameMode.ShadowMultiplayer) SpawnGhost();
    }

    private void SpawnGhost()
    {
        if (_ghostSpawned) return;
        if (ghostPrefab == null) { Debug.LogWarning("[PlayerMovement] ghostPrefab не назначен!"); return; }

        NetworkObject netObj = ghostPrefab.GetComponent<NetworkObject>();
        if (netObj == null) { Debug.LogError("[PlayerMovement] ghostPrefab без NetworkObject!"); return; }

        NetworkObject ghostObj = Instantiate(netObj, transform.position, transform.rotation);
        ghostObj.SpawnWithOwnership(OwnerClientId);

        _spawnedGhost = ghostObj.GetComponent<GhostSync>();
        _ghostSpawned = true;

        if (_spawnedGhost != null) _spawnedGhost.SetPlayerTransform(transform);
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged -= OnGameModeChanged;

        if (IsServer && _spawnedGhost != null && _spawnedGhost.IsSpawned)
        {
            _spawnedGhost.NetworkObject.Despawn(true);
            _spawnedGhost = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsOwner) return;

        if (_gameState != null && _gameState.IsPaused)
        {
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity  = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
            }
            return;
        }

        if (rb != null && rb.isKinematic) rb.isKinematic = false;

        bool wasGrounded = _isGrounded;
        _isGrounded = Physics.Raycast(transform.position, Vector3.down, 1.1f);

        if (_isGrounded && !wasGrounded)
        {
            _lastLandTime   = Time.time;
            _groundedTimer  = 0f;

            // Количество прыжков из StatSheet (с учётом CharacterDefinition.extraJumps)
            int effectiveMaxJumps = GetEffectiveMaxJumps();
            _jumpsRemaining = effectiveMaxJumps - 1;
        }
        else if (_isGrounded) _groundedTimer += Time.deltaTime;
        else                   _groundedTimer  = 0f;

        bool slideInput = Input.GetKey(KeyCode.LeftControl);
        bool onSlope    = false;

        if (_isGrounded && slideInput)
        {
            if (Physics.Raycast(transform.position, Vector3.down, out _slideGroundHit, 1.6f))
                onSlope = Vector3.Angle(_slideGroundHit.normal, Vector3.up) > slideAngleMin;
        }
        _isSliding = slideInput && _isGrounded && onSlope;

        rb.linearDamping = _isSliding ? slideDrag : (_isGrounded ? groundDrag : airDrag);

        if (Input.GetButtonDown("Jump"))
        {
            int effectiveMaxJumps = GetEffectiveMaxJumps();
            bool canJump = _isGrounded || _jumpsRemaining > 0;
            if (canJump)
            {
                rb.linearDamping  = 0f;
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

                // Высота прыжка из StatSheet (CharacterDefinition.jumpHeight влияет через JumpForceMultiplier)
                float jumpMult = _playerStats?.StatSheet?.GetStat(StatType.JumpForceMultiplier) ?? 1f;
                float force = (_isGrounded ? jumpForce : jumpForce * airJumpForceMultiplier) * jumpMult;
                rb.AddForce(Vector3.up * force, ForceMode.Impulse);

                if (_isGrounded) _jumpsRemaining = effectiveMaxJumps - 1;
                else             _jumpsRemaining--;

                _groundedTimer = 0f;
                _isGrounded    = false;
            }
        }

        if (Time.time >= _nextPickupCheckTime)
        {
            CollectItems();
            _nextPickupCheckTime = Time.time + PICKUP_INTERVAL;
        }

        if (_capsule != null && slideColliderHeight > 0f)
        {
            float targetH   = _isSliding ? slideColliderHeight : _defaultColHeight;
            _capsule.height = Mathf.Lerp(_capsule.height, targetH, Time.deltaTime * 12f);
        }
    }

    /// <summary>
    /// Возвращает максимальное количество прыжков.
    /// Читает ExtraJumps из StatSheet (CharacterDefinition.extraJumps).
    /// Fallback: maxJumpCount из Inspector.
    /// </summary>
    private int GetEffectiveMaxJumps()
    {
        if (_playerStats?.StatSheet == null) return maxJumpCount;
        int extra = Mathf.RoundToInt(_playerStats.StatSheet.GetStat(StatType.ExtraJumps));
        return Mathf.Max(1, 1 + extra);
    }

    void CollectItems()
    {
        // Радиус подбора из StatSheet (тотем добавляет Multiplicative-модификатор)
        float effectivePickupRange = _playerStats?.StatSheet != null
            ? _playerStats.StatSheet.GetStat(StatType.PickupRange)
            : pickupRange;

        var physScene = gameObject.scene.GetPhysicsScene();
        int count = physScene.OverlapSphere(
            transform.position, effectivePickupRange,
            _overlapBuffer, OrbLayerMask, QueryTriggerInteraction.Collide);

        ulong myClientId = IsSpawned ? OwnerClientId : ulong.MaxValue;

        for (int i = 0; i < count; i++)
        {
            if (!_overlapBuffer[i].CompareTag("Experience")) continue;
            ExperienceOrb orb = _overlapBuffer[i].GetComponent<ExperienceOrb>();
            if (orb == null) continue;
            if (orb.OwnerClientId != ulong.MaxValue && orb.OwnerClientId != myClientId) continue;
            orb.StartAttract(transform);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIXED UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    void FixedUpdate()
    {
        if (!IsOwner) return;
        if (_gameState != null && _gameState.IsPaused) return;
        MovePlayer();
    }

    void MovePlayer()
    {
        if (cam == null) return;

        // MoveSpeedMultiplier из StatSheet
        // 1.0 = стандарт, 0.85 = Воин медленнее, 1.15 = Маг быстрее
        float moveSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.MoveSpeedMultiplier) ?? 1f;

        if (_isSliding)
        {
            Vector3 slideDir = Vector3.ProjectOnPlane(Vector3.down, _slideGroundHit.normal).normalized;
            rb.AddForce(slideDir * slideForce, ForceMode.Force);
            Vector3 flatSlide = new Vector3(slideDir.x, 0f, slideDir.z);
            if (flatSlide.sqrMagnitude > 0.01f)
                transform.forward = Vector3.Slerp(transform.forward, flatSlide, Time.deltaTime * 8f);
            return;
        }

        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 camForward = cam.forward; camForward.y = 0f; camForward.Normalize();
        Vector3 camRight   = cam.right;   camRight.y   = 0f; camRight.Normalize();
        Vector3 moveDir    = (camForward * z + camRight * x).normalized;

        bool  inKnockbackSlow = Time.time < _knockbackSlowUntil;
        float speedMult       = inKnockbackSlow ? knockbackSpeedMultiplier : 1f;

        float effectiveWalk = walkSpeed   * moveSpeedMult * speedMult;
        float effectiveAir  = maxAirSpeed * moveSpeedMult * speedMult;

        if (moveDir.magnitude > 0.1f)
        {
            transform.forward = Vector3.Slerp(transform.forward, moveDir, Time.deltaTime * 12f);

            if (_isGrounded)
            {
                rb.AddForce(moveDir * (acceleration * moveSpeedMult * speedMult), ForceMode.Force);
            }
            else
            {
                Vector3 flatVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
                float velInDir  = Vector3.Dot(flatVel, moveDir);
                if (velInDir < effectiveAir)
                {
                    float addSpeed = Mathf.Min(
                        airAcceleration * moveSpeedMult * speedMult * Time.fixedDeltaTime,
                        effectiveAir - velInDir);
                    rb.AddForce(moveDir * (addSpeed / Time.fixedDeltaTime), ForceMode.Force);
                }
            }
        }

        Vector3 flatVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

        if (_isGrounded)
        {
            bool inBhopWindow = !inKnockbackSlow && (Time.time - _lastLandTime) < bhopGraceTime;
            if (!inBhopWindow && flatVelocity.magnitude > effectiveWalk)
            {
                Vector3 limited   = flatVelocity.normalized * effectiveWalk;
                rb.linearVelocity = new Vector3(limited.x, rb.linearVelocity.y, limited.z);
            }
        }
        else
        {
            if (flatVelocity.magnitude > effectiveAir)
            {
                Vector3 limited   = flatVelocity.normalized * effectiveAir;
                rb.linearVelocity = new Vector3(limited.x, rb.linearVelocity.y, limited.z);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KNOCKBACK
    // ─────────────────────────────────────────────────────────────────────────

    public void ApplyKnockback(Vector3 knockbackDir) => ApplyKnockback(knockbackDir, knockbackForce);

    public void ApplyKnockback(Vector3 knockbackDir, float customForce)
    {
        if (!IsLocalPlayer()) return;
        if (Time.time < _nextKnockbackTime) return;
        if (rb == null || rb.isKinematic) return;

        rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);

        Vector3 impulse;
        if (Mathf.Abs(knockbackDir.y) > 0.3f)
            impulse = knockbackDir * customForce;
        else
            impulse = knockbackDir * customForce + Vector3.up * (customForce * 0.15f);

        rb.AddForce(impulse, ForceMode.Impulse);

        _knockbackSlowUntil = Time.time + knockbackSlowDuration;
        _nextKnockbackTime  = Time.time + knockbackCooldown;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        float r = _playerStats?.StatSheet?.GetStat(StatType.PickupRange) ?? pickupRange;
        Gizmos.color = Color.blue;
        Gizmos.DrawWireSphere(transform.position, r);
    }
}
/*using UnityEngine;
using Unity.Netcode;
using VContainer;
using VContainer.Unity;

/// <summary>
/// PlayerMovement — движение игрока (Megabonk / bhop стиль).
///
/// ХАРАКТЕРИСТИКИ ИЗ STATSHEET:
///   MoveSpeedMultiplier  → умножается на walkSpeed и maxAirSpeed
///   ExtraJumps           → определяет maxJumpCount (0=1 прыжок, 1=2 прыжка...)
///   JumpForceMultiplier  → умножается на jumpForce (высота прыжка)
///   PickupRange          → радиус подбора XP-орбов
///
/// Формулы:
///   effectiveWalkSpeed = walkSpeed  * MoveSpeedMultiplier
///   effectiveAirSpeed  = maxAirSpeed * MoveSpeedMultiplier
///   effectiveJumpForce = jumpForce  * JumpForceMultiplier
///   maxJumps           = 1 + ExtraJumps  (1 = только с земли, 2 = земля+воздух)
/// </summary>
public class PlayerMovement : NetworkBehaviour
{
    [Header("Ссылки")]
    public Rigidbody rb;
    public Transform cam;
    public GameObject cameraPivotObject;

    [Header("Движение")]
    [Tooltip("Базовая скорость. Умножается на MoveSpeedMultiplier из StatSheet.")]
    public float walkSpeed = 10f;
    [Tooltip("Максимальная скорость в воздухе. Умножается на MoveSpeedMultiplier.")]
    public float maxAirSpeed = 26f;
    public float acceleration    = 80f;
    public float airAcceleration = 40f;
    public float jumpForce       = 9f;
    public float groundDrag      = 7f;
    public float airDrag         = 0.05f;

    [Header("Мультипрыжок (Inspector — дефолт без персонажа)")]
    [Tooltip("Используется как ЗАПАСНОЕ значение. StatSheet.ExtraJumps имеет приоритет.")]
    public int   maxJumpCount          = 2;
    public float airJumpForceMultiplier = 0.85f;

    [Header("Банихоп")]
    public float bhopGraceTime = 0.12f;

    [Header("Скольжение (CTRL)")]
    public float slideAngleMin       = 12f;
    public float slideForce          = 20f;
    public float slideDrag           = 0.5f;
    public float slideColliderHeight = 1.0f;

    [Header("Подбор орбов")]
    [Tooltip("Базовый радиус. Заменяется StatSheet.PickupRange если доступен.")]
    public float pickupRange = 5f;

    [Header("Ghost Prefab (Competitive)")]
    [SerializeField] private GameObject ghostPrefab;

    [Header("Knockback")]
    public float knockbackForce          = 8f;
    public float knockbackCooldown       = 0.2f;
    public float knockbackSlowDuration   = 1.2f;
    [Range(0.1f, 1f)]
    public float knockbackSpeedMultiplier = 0.35f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private float    _nextKnockbackTime;
    private float    _knockbackSlowUntil;
    private GhostSync _spawnedGhost  = null;
    private bool     _ghostSpawned   = false;

    private bool  _isGrounded    = false;
    private float _groundedTimer = 0f;
    private float _lastLandTime  = -999f;
    private int   _jumpsRemaining = 0;

    private bool      _isSliding       = false;
    private RaycastHit _slideGroundHit;
    private CapsuleCollider _capsule;
    private float     _defaultColHeight;

    private float _nextPickupCheckTime = 0f;
    private const float PICKUP_INTERVAL = 0.1f;

    private static readonly Collider[] _overlapBuffer = new Collider[64];
    private static int _orbLayerMask = -1;
    private static int OrbLayerMask
    {
        get
        {
            if (_orbLayerMask == -1)
            {
                int layer = LayerMask.NameToLayer("ExperienceOrb");
                _orbLayerMask = layer >= 0 ? (1 << layer) : ~0;
                if (layer < 0) Debug.LogWarning("[PlayerMovement] Слой 'ExperienceOrb' не найден!");
            }
            return _orbLayerMask;
        }
    }

    // ─── ЗАВИСИМОСТИ ──────────────────────────────────────────────────────────

    private GameStateService _gameState;
    private PlayerStats      _playerStats;

    [Inject]
    public void Construct(GameStateService gameState)
    {
        _gameState = gameState;
    }

    private new bool IsLocalPlayer()
    {
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return true;
        return IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NETWORK SPAWN
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        try { InjectionProvider.Container?.InjectGameObject(gameObject); }
        catch (System.Exception ex) { Debug.LogError($"[PlayerMovement] {ex.Message}"); }

        _playerStats = GetComponent<PlayerStats>();
        if (_playerStats == null)
            Debug.LogWarning("[PlayerMovement] PlayerStats не найден — MoveSpeed/Jump из StatSheet не будут работать.");

        if (IsOwner)
        {
            if (cameraPivotObject == null)
            {
                Transform foundPivot = transform.Find("CameraPivot");
                if (foundPivot != null) { cameraPivotObject = foundPivot.gameObject; cam = foundPivot; }
            }

            if (cameraPivotObject != null)
            {
                cameraPivotObject.SetActive(true);
                cam = cameraPivotObject.transform;
            }

            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule != null) _defaultColHeight = _capsule.height;
        }
        else
        {
            foreach (var r in GetComponentsInChildren<Renderer>())     r.enabled = false;
            foreach (var c in GetComponentsInChildren<Camera>(true))   c.gameObject.SetActive(false);

            if (cameraPivotObject != null) cameraPivotObject.SetActive(false);
            Transform pivot = transform.Find("CameraPivot");
            if (pivot != null) pivot.gameObject.SetActive(false);

            if (rb != null) { rb.isKinematic = true; rb.detectCollisions = false; }
            foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = false;
        }

        if (IsServer && GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;

        if (IsServer && GameModeManager.IsCompetitiveMode())
            SpawnGhost();
    }

    private void OnGameModeChanged(GameMode newMode)
    {
        if (!IsServer || _ghostSpawned) return;
        if (newMode == GameMode.ShadowMultiplayer) SpawnGhost();
    }

    private void SpawnGhost()
    {
        if (_ghostSpawned) return;
        if (ghostPrefab == null) { Debug.LogWarning("[PlayerMovement] ghostPrefab не назначен!"); return; }

        NetworkObject netObj = ghostPrefab.GetComponent<NetworkObject>();
        if (netObj == null) { Debug.LogError("[PlayerMovement] ghostPrefab без NetworkObject!"); return; }

        NetworkObject ghostObj = Instantiate(netObj, transform.position, transform.rotation);
        ghostObj.SpawnWithOwnership(OwnerClientId);

        _spawnedGhost = ghostObj.GetComponent<GhostSync>();
        _ghostSpawned = true;

        if (_spawnedGhost != null) _spawnedGhost.SetPlayerTransform(transform);
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged -= OnGameModeChanged;

        if (IsServer && _spawnedGhost != null && _spawnedGhost.IsSpawned)
        {
            _spawnedGhost.NetworkObject.Despawn(true);
            _spawnedGhost = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsOwner) return;

        if (_gameState != null && _gameState.IsPaused)
        {
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity  = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
            }
            return;
        }

        if (rb != null && rb.isKinematic) rb.isKinematic = false;

        bool wasGrounded = _isGrounded;
        _isGrounded = Physics.Raycast(transform.position, Vector3.down, 1.1f);

        if (_isGrounded && !wasGrounded)
        {
            _lastLandTime   = Time.time;
            _groundedTimer  = 0f;

            // Количество прыжков из StatSheet (с учётом CharacterDefinition.extraJumps)
            int effectiveMaxJumps = GetEffectiveMaxJumps();
            _jumpsRemaining = effectiveMaxJumps - 1;
        }
        else if (_isGrounded) _groundedTimer += Time.deltaTime;
        else                   _groundedTimer  = 0f;

        bool slideInput = Input.GetKey(KeyCode.LeftControl);
        bool onSlope    = false;

        if (_isGrounded && slideInput)
        {
            if (Physics.Raycast(transform.position, Vector3.down, out _slideGroundHit, 1.6f))
                onSlope = Vector3.Angle(_slideGroundHit.normal, Vector3.up) > slideAngleMin;
        }
        _isSliding = slideInput && _isGrounded && onSlope;

        rb.linearDamping = _isSliding ? slideDrag : (_isGrounded ? groundDrag : airDrag);

        if (Input.GetButtonDown("Jump"))
        {
            int effectiveMaxJumps = GetEffectiveMaxJumps();
            bool canJump = _isGrounded || _jumpsRemaining > 0;
            if (canJump)
            {
                rb.linearDamping  = 0f;
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

                // Высота прыжка из StatSheet (CharacterDefinition.jumpHeight влияет через JumpForceMultiplier)
                float jumpMult = _playerStats?.StatSheet?.GetStat(StatType.JumpForceMultiplier) ?? 1f;
                float force = (_isGrounded ? jumpForce : jumpForce * airJumpForceMultiplier) * jumpMult;
                rb.AddForce(Vector3.up * force, ForceMode.Impulse);

                if (_isGrounded) _jumpsRemaining = effectiveMaxJumps - 1;
                else             _jumpsRemaining--;

                _groundedTimer = 0f;
                _isGrounded    = false;
            }
        }

        if (Time.time >= _nextPickupCheckTime)
        {
            CollectItems();
            _nextPickupCheckTime = Time.time + PICKUP_INTERVAL;
        }

        if (_capsule != null && slideColliderHeight > 0f)
        {
            float targetH   = _isSliding ? slideColliderHeight : _defaultColHeight;
            _capsule.height = Mathf.Lerp(_capsule.height, targetH, Time.deltaTime * 12f);
        }
    }

    /// <summary>
    /// Возвращает максимальное количество прыжков.
    /// Читает ExtraJumps из StatSheet (CharacterDefinition.extraJumps).
    /// Fallback: maxJumpCount из Inspector.
    /// </summary>
    private int GetEffectiveMaxJumps()
    {
        if (_playerStats?.StatSheet == null) return maxJumpCount;
        int extra = Mathf.RoundToInt(_playerStats.StatSheet.GetStat(StatType.ExtraJumps));
        return Mathf.Max(1, 1 + extra);
    }

    void CollectItems()
    {
        // Радиус подбора из StatSheet (тотем добавляет Multiplicative-модификатор)
        float effectivePickupRange = _playerStats?.StatSheet != null
            ? _playerStats.StatSheet.GetStat(StatType.PickupRange)
            : pickupRange;

        var physScene = gameObject.scene.GetPhysicsScene();
        int count = physScene.OverlapSphere(
            transform.position, effectivePickupRange,
            _overlapBuffer, OrbLayerMask, QueryTriggerInteraction.Collide);

        ulong myClientId = IsSpawned ? OwnerClientId : ulong.MaxValue;

        for (int i = 0; i < count; i++)
        {
            if (!_overlapBuffer[i].CompareTag("Experience")) continue;
            ExperienceOrb orb = _overlapBuffer[i].GetComponent<ExperienceOrb>();
            if (orb == null) continue;
            if (orb.OwnerClientId != ulong.MaxValue && orb.OwnerClientId != myClientId) continue;
            orb.StartAttract(transform);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIXED UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    void FixedUpdate()
    {
        if (!IsOwner) return;
        if (_gameState != null && _gameState.IsPaused) return;
        MovePlayer();
    }

    void MovePlayer()
    {
        if (cam == null) return;

        // MoveSpeedMultiplier из StatSheet
        // 1.0 = стандарт, 0.85 = Воин медленнее, 1.15 = Маг быстрее
        float moveSpeedMult = _playerStats?.StatSheet?.GetStat(StatType.MoveSpeedMultiplier) ?? 1f;

        if (_isSliding)
        {
            Vector3 slideDir = Vector3.ProjectOnPlane(Vector3.down, _slideGroundHit.normal).normalized;
            rb.AddForce(slideDir * slideForce, ForceMode.Force);
            Vector3 flatSlide = new Vector3(slideDir.x, 0f, slideDir.z);
            if (flatSlide.sqrMagnitude > 0.01f)
                transform.forward = Vector3.Slerp(transform.forward, flatSlide, Time.deltaTime * 8f);
            return;
        }

        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 camForward = cam.forward; camForward.y = 0f; camForward.Normalize();
        Vector3 camRight   = cam.right;   camRight.y   = 0f; camRight.Normalize();
        Vector3 moveDir    = (camForward * z + camRight * x).normalized;

        bool  inKnockbackSlow = Time.time < _knockbackSlowUntil;
        float speedMult       = inKnockbackSlow ? knockbackSpeedMultiplier : 1f;

        float effectiveWalk = walkSpeed   * moveSpeedMult * speedMult;
        float effectiveAir  = maxAirSpeed * moveSpeedMult * speedMult;

        if (moveDir.magnitude > 0.1f)
        {
            transform.forward = Vector3.Slerp(transform.forward, moveDir, Time.deltaTime * 12f);

            if (_isGrounded)
            {
                rb.AddForce(moveDir * (acceleration * moveSpeedMult * speedMult), ForceMode.Force);
            }
            else
            {
                Vector3 flatVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
                float velInDir  = Vector3.Dot(flatVel, moveDir);
                if (velInDir < effectiveAir)
                {
                    float addSpeed = Mathf.Min(
                        airAcceleration * moveSpeedMult * speedMult * Time.fixedDeltaTime,
                        effectiveAir - velInDir);
                    rb.AddForce(moveDir * (addSpeed / Time.fixedDeltaTime), ForceMode.Force);
                }
            }
        }

        Vector3 flatVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

        if (_isGrounded)
        {
            bool inBhopWindow = !inKnockbackSlow && (Time.time - _lastLandTime) < bhopGraceTime;
            if (!inBhopWindow && flatVelocity.magnitude > effectiveWalk)
            {
                Vector3 limited   = flatVelocity.normalized * effectiveWalk;
                rb.linearVelocity = new Vector3(limited.x, rb.linearVelocity.y, limited.z);
            }
        }
        else
        {
            if (flatVelocity.magnitude > effectiveAir)
            {
                Vector3 limited   = flatVelocity.normalized * effectiveAir;
                rb.linearVelocity = new Vector3(limited.x, rb.linearVelocity.y, limited.z);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KNOCKBACK
    // ─────────────────────────────────────────────────────────────────────────

    public void ApplyKnockback(Vector3 knockbackDir) => ApplyKnockback(knockbackDir, knockbackForce);

    public void ApplyKnockback(Vector3 knockbackDir, float customForce)
    {
        if (!IsLocalPlayer()) return;
        if (Time.time < _nextKnockbackTime) return;
        if (rb == null || rb.isKinematic) return;

        rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);

        Vector3 impulse;
        if (Mathf.Abs(knockbackDir.y) > 0.3f)
            impulse = knockbackDir * customForce;
        else
            impulse = knockbackDir * customForce + Vector3.up * (customForce * 0.15f);

        rb.AddForce(impulse, ForceMode.Impulse);

        _knockbackSlowUntil = Time.time + knockbackSlowDuration;
        _nextKnockbackTime  = Time.time + knockbackCooldown;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        float r = _playerStats?.StatSheet?.GetStat(StatType.PickupRange) ?? pickupRange;
        Gizmos.color = Color.blue;
        Gizmos.DrawWireSphere(transform.position, r);
    }
}
*/