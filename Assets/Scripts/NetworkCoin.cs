using UnityEngine;
using Unity.Netcode;

public class NetworkCoin : NetworkBehaviour
{
    // Метод срабатывает при столкновении
    private void OnTriggerEnter(Collider medical)
    {
        // Проверяем, что коснулся именно Игрок
        if (medical.CompareTag("Player"))
        {
            // Только Сервер может удалять сетевые объекты!
            if (IsServer)
            {
                // Удаляем объект из сети (он исчезнет у всех)
                GetComponent<NetworkObject>().Despawn();
            }
        }
    }
}