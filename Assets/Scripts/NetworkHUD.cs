using UnityEngine;
using Unity.Netcode;

public class NetworkHUD : MonoBehaviour
{
    // Variable para mostrar el ID del cliente local
    private ulong localClientId = ulong.MaxValue;

    void Start() {
        // Suscribirse para obtener el ID del cliente cuando esté disponible
        if (NetworkManager.Singleton != null) {
             NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
             NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

     void OnDestroy() {
         // Desuscribirse para evitar errores
         if (NetworkManager.Singleton != null) {
             NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
             NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
         }
     }

    private void OnClientConnected(ulong clientId) {
        // Comprobar si NOSOTROS somos el cliente que se conectó
        if (NetworkManager.Singleton.LocalClientId == clientId) {
            localClientId = clientId;
        }
    }
     private void OnClientDisconnected(ulong clientId) {
          if (NetworkManager.Singleton.LocalClientId == clientId) {
            localClientId = ulong.MaxValue; // Resetear al desconectar
        }
     }


    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 350)); // Aumentar altura un poco
        if (!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            StartButtons();
        }
        else
        {
            StatusLabels();

            // --- BOTONES DEL SERVIDOR (Para Debug y Pruebas) ---
            if (NetworkManager.Singleton.IsServer)
            {
                // Botón para cambiar color (Ejemplo simple, no asigna únicos)
                if (GUILayout.Button("Color Azul (Todos)"))
                {
                    var players = FindObjectsOfType<Player>();
                    foreach (var player in players)
                    {
                        if (player != null) player.NetworkColor.Value = Color.blue;
                    }
                }
                 if (GUILayout.Button("Color Rojo (Todos)"))
                {
                    var players = FindObjectsOfType<Player>();
                    foreach (var player in players)
                    {
                        if (player != null) player.NetworkColor.Value = Color.red;
                    }
                }

                // Botón para quitar vida
                if (GUILayout.Button("Quitar Vida (Todos)"))
                {
                    var players = FindObjectsOfType<Player>();
                    foreach (var player in players)
                    {
                         if (player != null)
                         {
                            int cantidad = Random.Range(10, 30);
                            player.QuitarVida(cantidad); // Llama al método público en Player
                         }
                    }
                }


            }
            // --- FIN BOTONES SERVIDOR ---

            // --- BOTONES DEL CLIENTE (Opciones Locales/Debug) ---
             if (NetworkManager.Singleton.IsClient) {
                 // Añadir botones para que el cliente fuerce RPCs si queremos testear
                 // ej. GUILayout.Button("Test Disparo") -> GetLocalPlayer().SubmitFire...
             }
            // --- FIN BOTONES CLIENTE ---
        }
        GUILayout.EndArea();
    }

    // Botones de Conexión
    static void StartButtons()
    {
        // --- IMPORTANTE: Usar Server y Client separados, NO Host ---
        if (GUILayout.Button("Server")) NetworkManager.Singleton.StartServer();
        if (GUILayout.Button("Client")) NetworkManager.Singleton.StartClient();
    }

    // Etiquetas de Estado
    void StatusLabels() // Quitar static para acceder a localClientId
    {
        var mode = NetworkManager.Singleton.IsHost ? "Host (No Recomendado)" :
                   NetworkManager.Singleton.IsServer ? "Server" : "Client";

        GUILayout.Label("Transport: " + NetworkManager.Singleton.NetworkConfig.NetworkTransport.GetType().Name);
        GUILayout.Label("Mode: " + mode);

        // Mostrar ID del cliente local si es aplicable
        if (NetworkManager.Singleton.IsClient) {
             if (localClientId != ulong.MaxValue) {
                GUILayout.Label("My Client ID: " + localClientId);
             } else {
                 GUILayout.Label("Connecting...");
             }
        }
         // Mostrar número de clientes conectados si somos servidor
         if (NetworkManager.Singleton.IsServer) {
             GUILayout.Label("Clients Connected: " + NetworkManager.Singleton.ConnectedClients.Count);
         }
    }
}
