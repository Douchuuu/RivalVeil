using UnityEngine;
using VContainer;

/// <summary>
/// CameraOrbit v4 — ИСПРАВЛЕНО: ждём terrain, жёсткое ограничение границ.
///
/// ИСПРАВЛЕНИЯ v4:
///   ✦ Повторная попытка найти terrain если не найден при старте
///   ✦ Жёсткое ограничение позиции (без Lerp)
///   ✦ Ограничение по высоте terrain в текущей позиции
///   ✦ Добавлена защита от null player
/// </summary>
public class CameraOrbit : MonoBehaviour
{
    public Transform player;
    public float     sensitivity = 200f;

    [Header("Ограничение границ")]
    [Tooltip("Отступ от края terrain в метрах. Камера не будет выходить за эту границу.")]
    [SerializeField] private float cameraBoundaryMargin = 5f;

    [Tooltip("Минимальная высота камеры над terrain в текущей позиции")]
    [SerializeField] private float minHeightAboveTerrain = 1.5f;

    [Tooltip("Максимальная высота камеры над terrain в текущей позиции")]
    [SerializeField] private float maxHeightAboveTerrain = 50f;

    [Header("Инициализация")]
    [Tooltip("Максимальное время ожидания terrain (сек)")]
    [SerializeField] private float maxTerrainWaitTime = 3f;

    private float xRotation = 0f;
    private float yRotation = 0f;

    // ─── ЗАВИСИМОСТЬ — GameStateService ──────────────────────────────────────
    private GameStateService _gameState;

    // Кэшированные данные terrain
    private Terrain _cachedTerrain;
    private Vector3 _terrainMinBounds;
    private Vector3 _terrainMaxBounds;
    private float _terrainSizeX;
    private float _terrainSizeZ;
    private Vector3 _terrainPos;
    private bool _terrainInitialized = false;
    private float _terrainWaitTimer = 0f;

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;

        // Если забыл привязать игрока в инспекторе, попробуем найти родителя
        if (player == null) player = transform.parent;

        // Получаем GameStateService
        _gameState = InjectionProvider.Container?.Resolve<GameStateService>();

        // Пробуем инициализировать terrain
        InitializeTerrainBounds();
    }

    /// <summary>
    /// Инициализирует границы terrain для ограничения камеры
    /// </summary>
    private bool InitializeTerrainBounds()
    {
        _cachedTerrain = Terrain.activeTerrain;

        if (_cachedTerrain != null)
        {
            TerrainData tData = _cachedTerrain.terrainData;
            if (tData != null)
            {
                _terrainPos = _cachedTerrain.transform.position;
                Vector3 terrainSize = tData.size;
                _terrainSizeX = terrainSize.x;
                _terrainSizeZ = terrainSize.z;

                // Вычисляем границы с учётом отступа
                _terrainMinBounds = new Vector3(
                    _terrainPos.x + cameraBoundaryMargin,
                    _terrainPos.y,
                    _terrainPos.z + cameraBoundaryMargin
                );

                _terrainMaxBounds = new Vector3(
                    _terrainPos.x + terrainSize.x - cameraBoundaryMargin,
                    _terrainPos.y + terrainSize.y,
                    _terrainPos.z + terrainSize.z - cameraBoundaryMargin
                );

                _terrainInitialized = true;

                Debug.Log($"[CameraOrbit] Границы камеры: X[{_terrainMinBounds.x:F1}, {_terrainMaxBounds.x:F1}], " +
                          $"Z[{_terrainMinBounds.z:F1}, {_terrainMaxBounds.z:F1}], Margin={cameraBoundaryMargin}m");
                return true;
            }
        }

        return false;
    }

    void LateUpdate()
    {
        if (player == null) return;

        // Проверка паузы
        if (_gameState != null && _gameState.IsPaused) return;

        // ИСПРАВЛЕНИЕ: Ждём terrain если ещё не инициализирован
        if (!_terrainInitialized)
        {
            _terrainWaitTimer += Time.deltaTime;
            if (_terrainWaitTimer < maxTerrainWaitTime)
            {
                if (InitializeTerrainBounds())
                {
                    _terrainInitialized = true;
                }
                else
                {
                    // Terrain ещё не готов — просто следуем за игроком без ограничений
                    transform.position = player.position + new Vector3(0, 1.5f, 0);
                    return;
                }
            }
            else
            {
                // Время ожидания истекло — работаем без ограничений
                _terrainInitialized = true; // Чтобы не проверять каждый кадр
                Debug.LogWarning("[CameraOrbit] Terrain не найден после ожидания — ограничение границ не работает.");
            }
        }

        // Считываем мышь
        float mouseX = Input.GetAxis("Mouse X") * sensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * sensitivity * Time.deltaTime;

        yRotation += mouseX;
        xRotation -= mouseY;
        xRotation  = Mathf.Clamp(xRotation, -30f, 60f);

        // Применяем вращение
        transform.rotation = Quaternion.Euler(xRotation, yRotation, 0f);

        // Жёсткое ограничение позиции
        Vector3 desiredPosition = player.position + new Vector3(0, 1.5f, 0);
        Vector3 clampedPosition = ClampPositionToTerrainBounds(desiredPosition);

        // Прямое присваивание — камера никогда не выйдет за границы
        transform.position = clampedPosition;
    }

    /// <summary>
    /// Ограничивает позицию в пределах границ terrain (жёсткое ограничение)
    /// </summary>
    private Vector3 ClampPositionToTerrainBounds(Vector3 position)
    {
        if (!_terrainInitialized || _cachedTerrain == null)
            return position;

        Vector3 clampedPos = position;

        // Жёсткое ограничение X и Z в пределах terrain
        clampedPos.x = Mathf.Clamp(clampedPos.x, _terrainMinBounds.x, _terrainMaxBounds.x);
        clampedPos.z = Mathf.Clamp(clampedPos.z, _terrainMinBounds.z, _terrainMaxBounds.z);

        // Ограничение по высоте на основе terrain в текущей позиции
        float terrainHeight = GetTerrainHeightAtPosition(clampedPos);
        float minY = terrainHeight + minHeightAboveTerrain;
        float maxY = terrainHeight + maxHeightAboveTerrain;

        clampedPos.y = Mathf.Clamp(clampedPos.y, minY, maxY);

        return clampedPos;
    }

    /// <summary>
    /// Получает высоту terrain в указанной позиции
    /// </summary>
    private float GetTerrainHeightAtPosition(Vector3 worldPos)
    {
        if (_cachedTerrain == null) return 0f;

        float sampleHeight = _cachedTerrain.SampleHeight(worldPos);
        return sampleHeight + _terrainPos.y;
    }

    /// <summary>
    /// Обновляет границы terrain (вызывать если terrain изменился)
    /// </summary>
    public void RefreshTerrainBounds()
    {
        InitializeTerrainBounds();
    }

    // Визуализация границ в редакторе
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        // Рисуем границы terrain
        if (_terrainInitialized && _cachedTerrain != null)
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);

            Vector3 center = new Vector3(
                (_terrainMinBounds.x + _terrainMaxBounds.x) * 0.5f,
                (_terrainMinBounds.y + _terrainMaxBounds.y) * 0.5f,
                (_terrainMinBounds.z + _terrainMaxBounds.z) * 0.5f
            );

            Vector3 size = new Vector3(
                _terrainMaxBounds.x - _terrainMinBounds.x,
                _terrainMaxBounds.y - _terrainMinBounds.y,
                _terrainMaxBounds.z - _terrainMinBounds.z
            );

            Gizmos.DrawWireCube(center, size);
        }

        // Рисуем линию от игрока до камеры
        if (player != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(player.position, transform.position);
        }
    }
}
