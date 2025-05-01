using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic; // Para Listas

// Este script debe estar en un GameObject con NetworkObject en la escena
public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; } // Singleton simple

    [Header("Configuraci�n de Inicio")]
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>(); // Asignar transforms vac�os en el editor
    [SerializeField] private List<Color> playerColors = new List<Color>();     // Definir colores en el editor

    // Listas para llevar la cuenta de lo disponible (solo en el servidor)
    private List<int> availableSpawnIndices = new List<int>();
    private List<int> availableColorIndices = new List<int>();

    private Dictionary<ulong, int> clientColorMap = new Dictionary<ulong, int>(); // Para saber qu� color tiene cada cliente

    private void Awake()
    {
        // Implementaci�n b�sica de Singleton
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
            // Opcional: DontDestroyOnLoad(gameObject); si persiste entre escenas
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Los clientes no gestionan esto, solo reciben datos.
            // Desactivar el componente si no somos el servidor para ahorrar recursos.
            // enabled = false; // Ojo: Awake todav�a se ejecuta. Mejor poner l�gica en Start o controlar con IsServer.
            return;
        }

        Debug.Log("GameManager (Servidor): Inicializando...");

        // Inicializar listas de �ndices disponibles en el servidor
        availableSpawnIndices.Clear();
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            availableSpawnIndices.Add(i);
        }

        availableColorIndices.Clear();
        for (int i = 0; i < playerColors.Count; i++)
        {
            availableColorIndices.Add(i);
        }

        // Suscribirse a la conexi�n de clientes
        NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;

        // Opcional: Asignar datos a jugadores ya conectados si el GameManager spawnea tarde
        foreach (var kvp in NetworkManager.Singleton.ConnectedClients) {
             if (kvp.Value.PlayerObject != null) { // Asegurarse que el jugador ya spawne�
                 AssignDataToPlayer(kvp.Key);
             }
        }

        Debug.Log($"GameManager (Servidor): Listo. Spawns: {spawnPoints.Count}, Colores: {playerColors.Count}");
    }

    public override void OnNetworkDespawn()
    {
        // Desuscribirse para evitar errores
        if (IsServer)
        {
             if (NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
             }
        }
        if (Instance == this) Instance = null; // Limpiar singleton
        base.OnNetworkDespawn();
    }


    private void HandleClientConnected(ulong clientId)
    {
        // Se ejecuta en el servidor cuando un cliente se conecta
        Debug.Log($"GameManager (Servidor): Cliente {clientId} conectado. Intentando asignar datos.");
        AssignDataToPlayer(clientId);
    }

     private void HandleClientDisconnected(ulong clientId)
    {
        // Se ejecuta en el servidor cuando un cliente se desconecta
        Debug.Log($"GameManager (Servidor): Cliente {clientId} desconectado. Liberando recursos.");
        // Liberar el color y spawn point si es necesario (implementaci�n simple)
         if (clientColorMap.TryGetValue(clientId, out int colorIndex)) {
             if (!availableColorIndices.Contains(colorIndex)) { // Solo si no estaba ya libre
                 availableColorIndices.Add(colorIndex);
             }
             clientColorMap.Remove(clientId);
         }
         // Nota: Liberar spawn points es m�s complejo si no mapeamos cliente a spawn point.
         // Una soluci�n simple es no liberarlos y esperar a que haya hueco.
         // Para liberarlos, necesitar�amos un Dictionary<ulong, int> para spawn points tambi�n.
    }

    private void AssignDataToPlayer(ulong clientId)
    {
        if (!IsServer) return;

        // Esperar a que el objeto del jugador exista
        NetworkObject playerNetworkObject = NetworkManager.Singleton.ConnectedClients[clientId].PlayerObject;
        if (playerNetworkObject == null) {
             Debug.LogWarning($"GameManager (Servidor): PlayerObject para cliente {clientId} a�n no est� listo. Se reintentar�.");
             // Podr�amos usar una corutina para reintentar, o esperar al OnNetworkSpawn del Player.
             // Por simplicidad, asumimos que el Player llamar� a RequestInitialDataServerRpc.
             return;
        }

        Player playerScript = playerNetworkObject.GetComponent<Player>();
        if (playerScript == null)
        {
            Debug.LogError($"GameManager (Servidor): PlayerObject para cliente {clientId} no tiene script Player.");
            return;
        }

        // --- 1. Asignar Color �nico ---
        if (availableColorIndices.Count > 0)
        {
            int randomIndex = Random.Range(0, availableColorIndices.Count);
            int colorIndex = availableColorIndices[randomIndex];
            availableColorIndices.RemoveAt(randomIndex); // Quitarlo de disponibles

            playerScript.NetworkColor.Value = playerColors[colorIndex];
            clientColorMap[clientId] = colorIndex; // Guardar mapeo
            Debug.Log($"GameManager (Servidor): Asignado color {playerColors[colorIndex]} (�ndice {colorIndex}) a cliente {clientId}");
        }
        else
        {
            Debug.LogWarning($"GameManager (Servidor): No hay m�s colores disponibles para cliente {clientId}. Usando color por defecto.");
            playerScript.NetworkColor.Value = Color.white; // Color por defecto
        }

        // --- 2. Asignar Posici�n de Spawn �nica ---
        if (availableSpawnIndices.Count > 0)
        {
            int randomIndex = Random.Range(0, availableSpawnIndices.Count);
            int spawnIndex = availableSpawnIndices[randomIndex];
            availableSpawnIndices.RemoveAt(randomIndex); // Quitarlo de disponibles

            Vector3 spawnPosition = spawnPoints[spawnIndex].position;
            // Mover el jugador en el servidor (NetworkTransform sincronizar�)
            playerNetworkObject.transform.position = spawnPosition;
            playerNetworkObject.transform.rotation = spawnPoints[spawnIndex].rotation; // Tambi�n la rotaci�n
            Debug.Log($"GameManager (Servidor): Asignado spawn point {spawnIndex} ({spawnPosition}) a cliente {clientId}");

            // Opcional: Forzar teletransporte en el cliente si NetworkTransform tarda
            // playerScript.TeleportClientRpc(spawnPosition, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } });
        }
        else
        {
            Debug.LogWarning($"GameManager (Servidor): No hay m�s spawn points disponibles para cliente {clientId}. Usando posici�n (0,1,0).");
            playerNetworkObject.transform.position = Vector3.up; // Posici�n por defecto
        }
    }

    // Funci�n p�blica para que otros scripts (como Bullet) obtengan el color de un jugador
    public Color GetPlayerColor(ulong clientId) {
         if (clientColorMap.TryGetValue(clientId, out int colorIndex)) {
             if (colorIndex >= 0 && colorIndex < playerColors.Count) {
                 return playerColors[colorIndex];
             }
         }
         // Devolver un color por defecto si no se encuentra
         Debug.LogWarning($"GameManager: No se encontr� color asignado para cliente {clientId}. Devolviendo blanco.");
         return Color.white;
    }

     // Funci�n para obtener un spawn point aleatorio (ej. para respawn)
     public Vector3 GetRandomSpawnPoint() {
         if (spawnPoints.Count == 0) return Vector3.up;
         int randomIndex = Random.Range(0, spawnPoints.Count);
         return spawnPoints[randomIndex].position;
     }
}
