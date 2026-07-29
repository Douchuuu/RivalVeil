using UnityEngine;
using Unity.Netcode;

/// <summary>
/// GameModeSpawner v1.1 — спавнит GameModeManager на сервере.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО (v1.1):
/// ══════════════════════════════════════════════════════════════════════
///   - Добавлено подробное логирование для диагностики.
///   - Улучшены проверки перед спавном.
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class GameModeSpawner : MonoBehaviour
{
    [Header("Префаб GameModeManager")]
    [Tooltip("Перетащи сюда префаб с GameModeManager и NetworkObject")]
    [SerializeField] private GameObject gameModeManagerPrefab;
    
    private bool _hasSpawned = false;

    void Start()
    {
        Debug.Log($"[GameModeSpawner] Start. NetworkManager.Singleton={(NetworkManager.Singleton != null ? "OK" : "NULL")}");
        
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[GameModeSpawner] ❌ NetworkManager.Singleton == null! " +
                           "Убедись что NetworkManager есть на сцене.");
            return;
        }

        // Подписываемся на случай, если сервер запустится позже
        NetworkManager.Singleton.OnServerStarted += SpawnManager;

        // Но на Dedicated Server сервер обычно УЖЕ запущен
        if (NetworkManager.Singleton.IsServer)
        {
            Debug.Log("[GameModeSpawner] Сервер уже запущен, спавним сразу.");
            SpawnManager();
        }
        else
        {
            Debug.Log("[GameModeSpawner] Это не сервер, ждём OnServerStarted...");
        }
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= SpawnManager;
        }
    }

    private void SpawnManager()
    {
        Debug.Log($"[GameModeSpawner] SpawnManager вызван. IsServer={NetworkManager.Singleton?.IsServer}, _hasSpawned={_hasSpawned}");
        
        // 1. Проверка на сервере (только сервер может спавнить)
        if (!NetworkManager.Singleton.IsServer) 
        {
            Debug.LogWarning("[GameModeSpawner] Не сервер, пропускаем спавн.");
            return;
        }

        // 2. Атомарная проверка, чтобы не спавнить дважды
        if (_hasSpawned)
        {
            Debug.Log("[GameModeSpawner] Уже спавнили, пропускаем.");
            return;
        }
        
        if (GameModeManager.Instance != null)
        {
            Debug.Log("[GameModeSpawner] GameModeManager.Instance уже существует, пропускаем.");
            _hasSpawned = true;
            return;
        }

        if (gameModeManagerPrefab == null)
        {
            Debug.LogError("[GameModeSpawner] ❌ Префаб не назначен в инспекторе! " +
                           "Перетащи префаб с GameModeManager в поле gameModeManagerPrefab.");
            return;
        }

        // 3. Создаем объект
        Debug.Log("[GameModeSpawner] Создаём GameModeManager...");
        GameObject go = Instantiate(gameModeManagerPrefab);

        // 4. Важно: получаем NetworkObject и спавним его по сети
        var netObj = go.GetComponent<NetworkObject>();
        if (netObj != null)
        {
            netObj.Spawn();
            _hasSpawned = true;
            Debug.Log("[GameModeSpawner] ✅ GameModeManager успешно заспавнен на сервере.");
        }
        else
        {
            Debug.LogError("[GameModeSpawner] ❌ На префабе GameModeManager отсутствует компонент NetworkObject! " +
                           "Добавь NetworkObject на префаб.");
            Destroy(go);
        }
    }
}
