using UnityEngine;
using Unity.Netcode;

/// <summary>
/// GhostSync — ИСПРАВЛЕНО v5: визуал Ghost в цвет персонажа с задержкой.
///
/// ═══════════════════════════════════════════════════════════════════
/// НОВОЕ (v5) — визуал Ghost:
/// ═══════════════════════════════════════════════════════════════════
///
///   ПРОБЛЕМА: Ghost линкуется через AutoLinkToPlayer() раньше чем
///   CharacterSelectManager.ApplySelectedCharacter() успевает отработать.
///   Поэтому GetBodyColor() возвращал белый цвет (дефолт).
///
///   ИСПРАВЛЕНИЕ: ApplyGhostVisualDelayed() — корутина которая:
///     1. Красит Ghost сразу (может быть белым — временно)
///     2. Ждёт 10 кадров (ApplyAfterSpawn ждёт 2 кадра + запас)
///     3. Перекрашивает Ghost с актуальным цветом персонажа
///
///   Ghost = тот же цвет что персонаж игрока но с alpha GHOST_ALPHA (0.35).
///   Warrior Ghost → полупрозрачный оранжевый
///   Wizard Ghost  → полупрозрачный синий
///
///   Тени у Ghost отключены (shadowCastingMode = Off).
///
/// ═══════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЕ (v4) — IsSpawned проверки в AutoLinkToPlayer:
/// ═══════════════════════════════════════════════════════════════════
///
///   При быстром рестарте матча Ghost деспавнится пока корутина ещё
///   работала. Добавлены проверки if (!IsSpawned) yield break.
///
/// ═══════════════════════════════════════════════════════════════════
/// ТРЕБОВАНИЯ К МАТЕРИАЛУ GHOST PREFAB:
/// ═══════════════════════════════════════════════════════════════════
///
///   Ghost должен иметь прозрачный материал, иначе alpha не сработает.
///   URP: Surface Type = Transparent, Blending Mode = Alpha.
///   Built-in: Rendering Mode = Transparent.
/// </summary>
public class GhostSync : NetworkBehaviour
{
    // ─── СЕТЕВОЕ СОСТОЯНИЕ ───────────────────────────────────────────────────

    private NetworkVariable<GhostTransform> ghostTransform = new NetworkVariable<GhostTransform>(
        new GhostTransform { Position = Vector3.zero, Rotation = Quaternion.identity },
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    // Цвет тела персонажа — Owner пишет, Remote читает и красит Ghost.
    // Color32 blittable → сериализуется без кастомного NetworkSerialize.
    private NetworkVariable<Color32> ghostBodyColor = new NetworkVariable<Color32>(
        new Color32(255, 255, 255, 255),
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    // ─── КОНСТАНТЫ ───────────────────────────────────────────────────────────

    private const float SEND_INTERVAL = 0.0167f;
    private const float POSITION_THRESHOLD = 0.01f;
    private const float ROTATION_THRESHOLD = 0.3f;
    private const float INTERPOLATION_DELAY = 0.05f;
    private const float TELEPORT_THRESHOLD = 8f;

    /// <summary>
    /// Прозрачность Ghost.
    /// 0 = полностью прозрачный, 1 = непрозрачный.
    /// 0.35 = хорошо читается но явно "призрачный".
    /// </summary>
    private const float GHOST_ALPHA = 0.35f;

    // ─── SNAPSHOT БУФЕР ──────────────────────────────────────────────────────

    private struct GhostSnapshot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public double ServerTime;
    }

    private GhostSnapshot prevSnapshot;
    private GhostSnapshot nextSnapshot;
    private bool hasEnoughSnapshots = false;

    // ─── ДАННЫЕ ОТПРАВИТЕЛЯ ──────────────────────────────────────────────────

    private float nextSendTime = 0f;
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation;
    private Transform playerTransform;

    // ─────────────────────────────────────────────────────────────────────────
    // ПУБЛИЧНЫЙ МЕТОД ДЛЯ ПЕРЕДАЧИ ССЫЛКИ НА ИГРОКА
    // ─────────────────────────────────────────────────────────────────────────

    public void SetPlayerTransform(Transform t)
    {
        playerTransform = t;
        lastSentPosition = t.position;
        lastSentRotation = t.rotation;

        if (IsOwner)
        {
            ghostTransform.Value = new GhostTransform
            {
                Position = t.position,
                Rotation = SafeRotation(t.rotation)
            };
        }
        nextSendTime = 0f;

        // ИСПРАВЛЕНИЕ v5: используем корутину с задержкой.
        // Ghost линкуется раньше чем CharacterSelectManager применяет персонажа.
        // Ждём 10 кадров и перекрашиваем Ghost с актуальным цветом.
        StartCoroutine(ApplyGhostVisualDelayed(t));

        Debug.Log($"[GhostSync] Player transform linked at {t.position} (ClientId={OwnerClientId}, IsOwner={IsOwner})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ GHOST
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Корутина: ждёт пока CharacterSelectManager применит персонажа,
    /// затем пишет цвет в NetworkVariable. Remote получит его через OnValueChanged.
    /// </summary>
    private System.Collections.IEnumerator ApplyGhostVisualDelayed(Transform linkedTransform)
    {
        // Ждём пока ApplyAfterSpawn() отработает (он сам ждёт 2 кадра + запас)
        for (int i = 0; i < 10; i++)
        {
            yield return null;
            if (!IsSpawned) yield break;
        }

        ApplyGhostVisual(linkedTransform);
        Debug.Log($"[GhostSync] 🔄 Ghost цвет записан в NetworkVariable");
    }

    /// <summary>
    /// Owner: читает цвет из CharacterSelectManager и пишет в ghostBodyColor.
    /// Это единственное что делает Owner — рендеры у него выключены.
    /// </summary>
    private void ApplyGhostVisual(Transform linkedPlayerTransform)
    {
        if (linkedPlayerTransform == null) return;
        if (!IsOwner) return;

        Color bodyColor = Color.white;
        var charSelect = linkedPlayerTransform.GetComponent<CharacterSelectManager>();
        if (charSelect != null)
            bodyColor = charSelect.GetBodyColor();
        else
            Debug.LogWarning("[GhostSync] CharacterSelectManager не найден — Ghost будет белым.");

        // Пишем в NetworkVariable — Remote получит через OnGhostBodyColorChanged
        ghostBodyColor.Value = (Color32)bodyColor;
        Debug.Log($"[GhostSync] 👻 Ghost цвет отправлен: {bodyColor}");
    }

    /// <summary>
    /// Remote: красит все рендереры Ghost в полученный цвет с прозрачностью.
    /// Вызывается из OnGhostBodyColorChanged или при спавне если цвет уже есть.
    /// </summary>
    private void ApplyGhostColor(Color bodyColor)
    {
        Color ghostColor = new Color(bodyColor.r, bodyColor.g, bodyColor.b, GHOST_ALPHA);

        var renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            foreach (var mat in r.materials)
            {
                // URP
                if (mat.HasProperty("_Surface"))
                {
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.renderQueue = 3000;
                }
                // Built-in
                else if (mat.HasProperty("_Mode"))
                {
                    mat.SetFloat("_Mode", 3f);
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.renderQueue = 3000;
                }

                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", ghostColor);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", ghostColor);
                mat.color = ghostColor;
            }

            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        Debug.Log($"[GhostSync] 👻 Ghost покрашен (Remote): цвет={bodyColor} alpha={GHOST_ALPHA}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            foreach (var r in GetComponentsInChildren<Renderer>())
                r.enabled = false;

            StartCoroutine(AutoLinkToPlayer());

            Debug.Log($"[GhostSync] Spawned as owner (ClientId={OwnerClientId}). Waiting for AutoLink...");
            return;
        }

        var initial = ghostTransform.Value;
        double now = NetworkManager.Singleton.ServerTime.Time;

        prevSnapshot = new GhostSnapshot
        {
            Position = initial.Position,
            Rotation = SafeRotation(initial.Rotation),
            ServerTime = now
        };
        nextSnapshot = prevSnapshot;

        transform.position = initial.Position;
        transform.rotation = prevSnapshot.Rotation;

        Debug.Log($"[GhostSync] Spawned as remote at {initial.Position} (ClientId={OwnerClientId})");

        ghostTransform.OnValueChanged += OnGhostTransformChanged;
        ghostBodyColor.OnValueChanged += OnGhostBodyColorChanged;

        // Если цвет уже пришёл (late-join) — красим сразу
        Color32 c = ghostBodyColor.Value;
        if (c.r != 255 || c.g != 255 || c.b != 255)
            ApplyGhostColor((Color)c);
    }

    public override void OnNetworkDespawn()
    {
        ghostTransform.OnValueChanged -= OnGhostTransformChanged;
        ghostBodyColor.OnValueChanged -= OnGhostBodyColorChanged;
    }

    private void OnGhostBodyColorChanged(Color32 oldColor, Color32 newColor)
    {
        if (!IsOwner)
            ApplyGhostColor((Color)newColor);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АВТО-ЛИНК
    // ─────────────────────────────────────────────────────────────────────────

    private System.Collections.IEnumerator AutoLinkToPlayer()
    {
        if (!IsSpawned) yield break;

        yield return null;

        if (!IsSpawned) yield break;

        PlayerMovement[] allPlayers = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        foreach (var player in allPlayers)
        {
            if (player.OwnerClientId == OwnerClientId)
            {
                if (!IsSpawned) yield break;
                SetPlayerTransform(player.transform);
                Debug.Log($"[GhostSync] ✅ AutoLink успешен: PlayerMovement найден для ClientId={OwnerClientId}");
                yield break;
            }
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            yield return new WaitForSeconds(0.1f);

            if (!IsSpawned)
            {
                Debug.Log($"[GhostSync] AutoLink отменён (attempt {attempt + 1}): Ghost деспавнился.");
                yield break;
            }

            allPlayers = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
            foreach (var player in allPlayers)
            {
                if (player.OwnerClientId == OwnerClientId)
                {
                    if (!IsSpawned) yield break;
                    SetPlayerTransform(player.transform);
                    Debug.Log($"[GhostSync] ✅ AutoLink успешен (попытка {attempt + 2}): ClientId={OwnerClientId}");
                    yield break;
                }
            }
        }

        if (IsSpawned)
            Debug.LogError($"[GhostSync] ❌ AutoLink FAILED: PlayerMovement не найден для ClientId={OwnerClientId}!");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОТПРАВКА (OWNER)
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (IsOwner)
            SendUpdateIfNeeded();
        else
            UpdateLocalTransformWithInterpolation();
    }

    private void SendUpdateIfNeeded()
    {
        if (playerTransform == null) return;
        if (Time.time < nextSendTime) return;

        Vector3 newPos = playerTransform.position;
        Quaternion newRot = playerTransform.rotation;

        float posDelta = Vector3.Distance(newPos, lastSentPosition);
        float rotDelta = Quaternion.Angle(newRot, lastSentRotation);

        if (posDelta < POSITION_THRESHOLD && rotDelta < ROTATION_THRESHOLD) return;

        lastSentPosition = newPos;
        lastSentRotation = newRot;

        ghostTransform.Value = new GhostTransform
        {
            Position = newPos,
            Rotation = SafeRotation(newRot)
        };

        nextSendTime = Time.time + SEND_INTERVAL;
    }

    private void OnGhostTransformChanged(GhostTransform prev, GhostTransform curr)
    {
        if (IsOwner) return;

        prevSnapshot = nextSnapshot;
        nextSnapshot = new GhostSnapshot
        {
            Position = curr.Position,
            Rotation = SafeRotation(curr.Rotation),
            ServerTime = NetworkManager.Singleton.ServerTime.Time
        };

        hasEnoughSnapshots = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНТЕРПОЛЯЦИЯ (REMOTE)
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateLocalTransformWithInterpolation()
    {
        if (!hasEnoughSnapshots) return;

        float dist = Vector3.Distance(prevSnapshot.Position, nextSnapshot.Position);
        if (dist > TELEPORT_THRESHOLD)
        {
            transform.position = nextSnapshot.Position;
            transform.rotation = nextSnapshot.Rotation;
            return;
        }

        double renderTime = NetworkManager.Singleton.ServerTime.Time - INTERPOLATION_DELAY;
        double timeBetween = nextSnapshot.ServerTime - prevSnapshot.ServerTime;

        float t = timeBetween > 0.0001
            ? (float)((renderTime - prevSnapshot.ServerTime) / timeBetween)
            : 1f;

        t = Mathf.Clamp01(t);

        transform.position = Vector3.Lerp(prevSnapshot.Position, nextSnapshot.Position, t);
        transform.rotation = Quaternion.Slerp(prevSnapshot.Rotation, nextSnapshot.Rotation, t);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВСПОМОГАТЕЛЬНЫЕ
    // ─────────────────────────────────────────────────────────────────────────

    private static Quaternion SafeRotation(Quaternion q)
    {
        return (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
            ? Quaternion.identity
            : q;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public struct GhostTransform : System.IEquatable<GhostTransform>, INetworkSerializeByMemcpy
{
    public Vector3 Position;
    public Quaternion Rotation;

    public bool Equals(GhostTransform other) =>
        Position == other.Position && Rotation == other.Rotation;

    public override bool Equals(object obj) =>
        obj is GhostTransform g && Equals(g);

    public override int GetHashCode() =>
        Position.GetHashCode() ^ Rotation.GetHashCode();

    public static bool operator ==(GhostTransform lhs, GhostTransform rhs) => lhs.Equals(rhs);
    public static bool operator !=(GhostTransform lhs, GhostTransform rhs) => !lhs.Equals(rhs);
}
/*public class GhostSync : NetworkBehaviour
{
    // ─── СЕТЕВОЕ СОСТОЯНИЕ ───────────────────────────────────────────────────

    private NetworkVariable<GhostTransform> ghostTransform = new NetworkVariable<GhostTransform>(
        new GhostTransform { Position = Vector3.zero, Rotation = Quaternion.identity },
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    // Цвет тела персонажа — Owner пишет, Remote читает и красит Ghost.
    // Color32 blittable → сериализуется без кастомного NetworkSerialize.
    private NetworkVariable<Color32> ghostBodyColor = new NetworkVariable<Color32>(
        new Color32(255, 255, 255, 255),
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    // ─── КОНСТАНТЫ ───────────────────────────────────────────────────────────

    private const float SEND_INTERVAL = 0.0167f;
    private const float POSITION_THRESHOLD = 0.01f;
    private const float ROTATION_THRESHOLD = 0.3f;
    private const float INTERPOLATION_DELAY = 0.05f;
    private const float TELEPORT_THRESHOLD = 8f;

    /// <summary>
    /// Прозрачность Ghost.
    /// 0 = полностью прозрачный, 1 = непрозрачный.
    /// 0.35 = хорошо читается но явно "призрачный".
    /// </summary>
    private const float GHOST_ALPHA = 0.35f;

    // ─── SNAPSHOT БУФЕР ──────────────────────────────────────────────────────

    private struct GhostSnapshot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public double ServerTime;
    }

    private GhostSnapshot prevSnapshot;
    private GhostSnapshot nextSnapshot;
    private bool hasEnoughSnapshots = false;

    // ─── ДАННЫЕ ОТПРАВИТЕЛЯ ──────────────────────────────────────────────────

    private float nextSendTime = 0f;
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation;
    private Transform playerTransform;

    // ─────────────────────────────────────────────────────────────────────────
    // ПУБЛИЧНЫЙ МЕТОД ДЛЯ ПЕРЕДАЧИ ССЫЛКИ НА ИГРОКА
    // ─────────────────────────────────────────────────────────────────────────

    public void SetPlayerTransform(Transform t)
    {
        playerTransform = t;
        lastSentPosition = t.position;
        lastSentRotation = t.rotation;

        if (IsOwner)
        {
            ghostTransform.Value = new GhostTransform
            {
                Position = t.position,
                Rotation = SafeRotation(t.rotation)
            };
        }
        nextSendTime = 0f;

        // ИСПРАВЛЕНИЕ v5: используем корутину с задержкой.
        // Ghost линкуется раньше чем CharacterSelectManager применяет персонажа.
        // Ждём 10 кадров и перекрашиваем Ghost с актуальным цветом.
        StartCoroutine(ApplyGhostVisualDelayed(t));

        Debug.Log($"[GhostSync] Player transform linked at {t.position} (ClientId={OwnerClientId}, IsOwner={IsOwner})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ GHOST
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Корутина: ждёт пока CharacterSelectManager применит персонажа,
    /// затем пишет цвет в NetworkVariable. Remote получит его через OnValueChanged.
    /// </summary>
    private System.Collections.IEnumerator ApplyGhostVisualDelayed(Transform linkedTransform)
    {
        // Ждём пока ApplyAfterSpawn() отработает (он сам ждёт 2 кадра + запас)
        for (int i = 0; i < 10; i++)
        {
            yield return null;
            if (!IsSpawned) yield break;
        }

        ApplyGhostVisual(linkedTransform);
        Debug.Log($"[GhostSync] 🔄 Ghost цвет записан в NetworkVariable");
    }

    /// <summary>
    /// Owner: читает цвет из CharacterSelectManager и пишет в ghostBodyColor.
    /// Это единственное что делает Owner — рендеры у него выключены.
    /// </summary>
    private void ApplyGhostVisual(Transform linkedPlayerTransform)
    {
        if (linkedPlayerTransform == null) return;
        if (!IsOwner) return;

        Color bodyColor = Color.white;
        var charSelect = linkedPlayerTransform.GetComponent<CharacterSelectManager>();
        if (charSelect != null)
            bodyColor = charSelect.GetBodyColor();
        else
            Debug.LogWarning("[GhostSync] CharacterSelectManager не найден — Ghost будет белым.");

        // Пишем в NetworkVariable — Remote получит через OnGhostBodyColorChanged
        ghostBodyColor.Value = (Color32)bodyColor;
        Debug.Log($"[GhostSync] 👻 Ghost цвет отправлен: {bodyColor}");
    }

    /// <summary>
    /// Remote: красит все рендереры Ghost в полученный цвет с прозрачностью.
    /// Вызывается из OnGhostBodyColorChanged или при спавне если цвет уже есть.
    /// </summary>
    private void ApplyGhostColor(Color bodyColor)
    {
        Color ghostColor = new Color(bodyColor.r, bodyColor.g, bodyColor.b, GHOST_ALPHA);

        var renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            foreach (var mat in r.materials)
            {
                // URP
                if (mat.HasProperty("_Surface"))
                {
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.renderQueue = 3000;
                }
                // Built-in
                else if (mat.HasProperty("_Mode"))
                {
                    mat.SetFloat("_Mode", 3f);
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.renderQueue = 3000;
                }

                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", ghostColor);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", ghostColor);
                mat.color = ghostColor;
            }

            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        Debug.Log($"[GhostSync] 👻 Ghost покрашен (Remote): цвет={bodyColor} alpha={GHOST_ALPHA}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            foreach (var r in GetComponentsInChildren<Renderer>())
                r.enabled = false;

            StartCoroutine(AutoLinkToPlayer());

            Debug.Log($"[GhostSync] Spawned as owner (ClientId={OwnerClientId}). Waiting for AutoLink...");
            return;
        }

        var initial = ghostTransform.Value;
        double now = NetworkManager.Singleton.ServerTime.Time;

        prevSnapshot = new GhostSnapshot
        {
            Position = initial.Position,
            Rotation = SafeRotation(initial.Rotation),
            ServerTime = now
        };
        nextSnapshot = prevSnapshot;

        transform.position = initial.Position;
        transform.rotation = prevSnapshot.Rotation;

        Debug.Log($"[GhostSync] Spawned as remote at {initial.Position} (ClientId={OwnerClientId})");

        ghostTransform.OnValueChanged += OnGhostTransformChanged;
        ghostBodyColor.OnValueChanged += OnGhostBodyColorChanged;

        // Если цвет уже пришёл (late-join) — красим сразу
        Color32 c = ghostBodyColor.Value;
        if (c.r != 255 || c.g != 255 || c.b != 255)
            ApplyGhostColor((Color)c);
    }

    public override void OnNetworkDespawn()
    {
        ghostTransform.OnValueChanged -= OnGhostTransformChanged;
        ghostBodyColor.OnValueChanged -= OnGhostBodyColorChanged;
    }

    private void OnGhostBodyColorChanged(Color32 oldColor, Color32 newColor)
    {
        if (!IsOwner)
            ApplyGhostColor((Color)newColor);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АВТО-ЛИНК
    // ─────────────────────────────────────────────────────────────────────────

    private System.Collections.IEnumerator AutoLinkToPlayer()
    {
        if (!IsSpawned) yield break;

        yield return null;

        if (!IsSpawned) yield break;

        PlayerMovement[] allPlayers = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        foreach (var player in allPlayers)
        {
            if (player.OwnerClientId == OwnerClientId)
            {
                if (!IsSpawned) yield break;
                SetPlayerTransform(player.transform);
                Debug.Log($"[GhostSync] ✅ AutoLink успешен: PlayerMovement найден для ClientId={OwnerClientId}");
                yield break;
            }
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            yield return new WaitForSeconds(0.1f);

            if (!IsSpawned)
            {
                Debug.Log($"[GhostSync] AutoLink отменён (attempt {attempt + 1}): Ghost деспавнился.");
                yield break;
            }

            allPlayers = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
            foreach (var player in allPlayers)
            {
                if (player.OwnerClientId == OwnerClientId)
                {
                    if (!IsSpawned) yield break;
                    SetPlayerTransform(player.transform);
                    Debug.Log($"[GhostSync] ✅ AutoLink успешен (попытка {attempt + 2}): ClientId={OwnerClientId}");
                    yield break;
                }
            }
        }

        if (IsSpawned)
            Debug.LogError($"[GhostSync] ❌ AutoLink FAILED: PlayerMovement не найден для ClientId={OwnerClientId}!");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОТПРАВКА (OWNER)
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (IsOwner)
            SendUpdateIfNeeded();
        else
            UpdateLocalTransformWithInterpolation();
    }

    private void SendUpdateIfNeeded()
    {
        if (playerTransform == null) return;
        if (Time.time < nextSendTime) return;

        Vector3 newPos = playerTransform.position;
        Quaternion newRot = playerTransform.rotation;

        float posDelta = Vector3.Distance(newPos, lastSentPosition);
        float rotDelta = Quaternion.Angle(newRot, lastSentRotation);

        if (posDelta < POSITION_THRESHOLD && rotDelta < ROTATION_THRESHOLD) return;

        lastSentPosition = newPos;
        lastSentRotation = newRot;

        ghostTransform.Value = new GhostTransform
        {
            Position = newPos,
            Rotation = SafeRotation(newRot)
        };

        nextSendTime = Time.time + SEND_INTERVAL;
    }

    private void OnGhostTransformChanged(GhostTransform prev, GhostTransform curr)
    {
        if (IsOwner) return;

        prevSnapshot = nextSnapshot;
        nextSnapshot = new GhostSnapshot
        {
            Position = curr.Position,
            Rotation = SafeRotation(curr.Rotation),
            ServerTime = NetworkManager.Singleton.ServerTime.Time
        };

        hasEnoughSnapshots = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНТЕРПОЛЯЦИЯ (REMOTE)
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateLocalTransformWithInterpolation()
    {
        if (!hasEnoughSnapshots) return;

        float dist = Vector3.Distance(prevSnapshot.Position, nextSnapshot.Position);
        if (dist > TELEPORT_THRESHOLD)
        {
            transform.position = nextSnapshot.Position;
            transform.rotation = nextSnapshot.Rotation;
            return;
        }

        double renderTime = NetworkManager.Singleton.ServerTime.Time - INTERPOLATION_DELAY;
        double timeBetween = nextSnapshot.ServerTime - prevSnapshot.ServerTime;

        float t = timeBetween > 0.0001
            ? (float)((renderTime - prevSnapshot.ServerTime) / timeBetween)
            : 1f;

        t = Mathf.Clamp01(t);

        transform.position = Vector3.Lerp(prevSnapshot.Position, nextSnapshot.Position, t);
        transform.rotation = Quaternion.Slerp(prevSnapshot.Rotation, nextSnapshot.Rotation, t);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВСПОМОГАТЕЛЬНЫЕ
    // ─────────────────────────────────────────────────────────────────────────

    private static Quaternion SafeRotation(Quaternion q)
    {
        return (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
            ? Quaternion.identity
            : q;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public struct GhostTransform : System.IEquatable<GhostTransform>, INetworkSerializeByMemcpy
{
    public Vector3 Position;
    public Quaternion Rotation;

    public bool Equals(GhostTransform other) =>
        Position == other.Position && Rotation == other.Rotation;

    public override bool Equals(object obj) =>
        obj is GhostTransform g && Equals(g);

    public override int GetHashCode() =>
        Position.GetHashCode() ^ Rotation.GetHashCode();

    public static bool operator ==(GhostTransform lhs, GhostTransform rhs) => lhs.Equals(rhs);
    public static bool operator !=(GhostTransform lhs, GhostTransform rhs) => !lhs.Equals(rhs);
}
*/