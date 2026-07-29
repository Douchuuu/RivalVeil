using Unity.Netcode;
using UnityEngine;

public class NetworkManagerBootstrap : MonoBehaviour
{
    void Awake()
    {
        // Если NetworkManager уже существует в DDOL — уничтожаем дубликат
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.gameObject != gameObject)
        {
            Destroy(gameObject);
            return;
        }

        DontDestroyOnLoad(gameObject);
    }
}