using UnityEngine;
using Unity.Netcode;
using TMPro; // Necesario para TMP_Text
using UnityEngine.UI;
using Unity.Netcode.Components; // Necesario para Slider

// Añadimos el requisito de NetworkTransform
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
public class Player : NetworkBehaviour
{
    // --- NUEVAS VARIABLES ---
    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 3.0f; // Velocidad de movimiento

    [Header("Vida y UI")]
    [SerializeField] private GameObject healthBarPrefab; // Arrastra aquí el prefab de la barra de vida
    [SerializeField] private Transform healthBarAnchor; // Arrastra aquí el objeto hijo "HealthBarAnchor"
    private Slider healthBarSlider; // Referencia al slider de la barra flotante
    private GameObject healthBarInstance; // Referencia a la instancia de la barra flotante
    // --- FIN NUEVAS VARIABLES ---

    // Ya no necesitamos sincronizar la posición manualmente
    // public NetworkVariable<Vector3> Position = new NetworkVariable<Vector3>();

    // En este caso, un color
    public NetworkVariable<Color> NetworkColor = new NetworkVariable<Color>();

    // Variable de salud sincronizada. Añadimos permisos y valor inicial.
    public NetworkVariable<int> NetworkSalud = new NetworkVariable<int>(
        100, // Valor inicial de la vida
        NetworkVariableReadPermission.Everyone, // Todos pueden leer
        NetworkVariableWritePermission.Server); // Solo el servidor puede escribir

    private TMP_Text textoVida; // Referencia al texto de la UI local

    // Evento que se lanza cuando se hace spawn de un objeto
    public override void OnNetworkSpawn()
    {
        // Sincronización inicial de color (si es necesario que se vea al inicio)
        ChangeColor(NetworkColor.Value);

        if (IsOwner)
        {
            Debug.Log("Soy el propietario (IsOwner=true, IsLocalPlayer=true)");
            // Ya no llamamos a Move() aquí, el movimiento se gestiona en Update

            // Busco la interfaz de vida local
            var vidaPlayerObject = GameObject.Find("VidaPlayer"); // Busca el objeto por nombre
            if (vidaPlayerObject != null)
            {
                textoVida = vidaPlayerObject.GetComponent<TMP_Text>();
                if (textoVida == null)
                {
                    Debug.LogError("No se encontró el componente TMP_Text en el objeto 'VidaPlayer'");
                }
            }
            else
            {
                Debug.LogError("No se encontró el objeto 'VidaPlayer' en la escena para la UI local.");
            }

            // Actualizar la UI local con el valor inicial
            UpdateLocalHealthUI(NetworkSalud.Value);

            // Suscribir callback para cambios de vida (solo para UI local)
            NetworkSalud.OnValueChanged += OnHealthChangedOwner;
        }
        else
        {
            Debug.Log($"No soy el propietario (IsOwner=false), OwnerId: {OwnerClientId}");
            // Instanciar y configurar barra de vida flotante para otros jugadores
            if (healthBarPrefab != null && healthBarAnchor != null)
            {
                healthBarInstance = Instantiate(healthBarPrefab, healthBarAnchor.position, healthBarAnchor.rotation); // Usar rotación del anchor por si acaso
                healthBarInstance.transform.SetParent(healthBarAnchor, true); // Hacerlo hijo y mantener posición/rotación world
                healthBarSlider = healthBarInstance.GetComponentInChildren<Slider>();
                if (healthBarSlider == null)
                {
                    Debug.LogError("El prefab de la barra de vida no contiene un componente Slider en sus hijos.");
                }
                else
                {
                    // Actualizar la barra flotante con el valor inicial
                    UpdateFloatingHealthBar(NetworkSalud.Value);
                    // Suscribir callback para cambios de vida (para la barra flotante)
                    NetworkSalud.OnValueChanged += OnHealthChangedNonOwner;
                }
            }
            else
            {
                Debug.LogError("Falta asignar el prefab de la barra de vida o el anchor en el Inspector.");
            }
        }

        // Suscribirse al cambio de color (solo si no somos el servidor, ya que el servidor setea el valor)
        if (!IsServer)
        {
            NetworkColor.OnValueChanged += ColorChanged;
        }
    }

    // --- NUEVO: Callback para cambios de vida para el PROPIETARIO (actualiza UI local) ---
    private void OnHealthChangedOwner(int previousValue, int newValue)
    {
        UpdateLocalHealthUI(newValue);
    }

    // --- NUEVO: Callback para cambios de vida para los NO PROPIETARIOS (actualiza barra flotante) ---
    private void OnHealthChangedNonOwner(int previousValue, int newValue)
    {
        UpdateFloatingHealthBar(newValue);
    }

    // --- NUEVO: Método para actualizar la UI de vida LOCAL ---
    private void UpdateLocalHealthUI(int currentHealth)
    {
        if (textoVida != null)
        {
            textoVida.text = "HEALTH: " + currentHealth.ToString(); // Actualiza el texto
        }
    }

    // --- NUEVO: Método para actualizar la barra de vida FLOTANTE ---
    private void UpdateFloatingHealthBar(int currentHealth)
    {
        if (healthBarSlider != null)
        {
            // Asumiendo que la vida máxima es 100 para el porcentaje
            healthBarSlider.value = (float)currentHealth / 100.0f;
        }
        // Opcional: Ocultar la barra si la vida es 0 o menos
        if (healthBarInstance != null)
        {
            // healthBarInstance.SetActive(currentHealth > 0); // Descomentar si quieres ocultarla al morir
        }
    }

    // Cambiar el color al MeshRenderer
    public void ChangeColor(Color color)
    {
        var meshRenderer = this.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            meshRenderer.material.color = color;
        }
        else
        {
            Debug.LogWarning("MeshRenderer no encontrado en el jugador.");
        }
    }

    // // Callback original, ahora separado en OnHealthChangedOwner y OnHealthChangedNonOwner
    // public void CambioVida(int vidaAnterior, int nuevaVida)
    // {
    //     Debug.Log("Vida went from " + vidaAnterior + " to " + nuevaVida);
    //     // Tiene que hacer el cambio que ve el cliente
    //     if (IsOwner && textoVida != null) // Asegurarse de que solo el owner actualiza su texto
    //     {
    //         textoVida.text = nuevaVida.ToString();
    //     }
    //     // Aquí iría la lógica para actualizar la barra flotante si !IsOwner
    // }

    // Método para que el servidor reduzca la vida
    public void QuitarVida(int cantidad)
    {
        if (IsServer)
        {
            // Asegurarse de que la vida no sea negativa
            NetworkSalud.Value = Mathf.Max(0, NetworkSalud.Value - cantidad);
            Debug.Log($"Servidor: Quitada vida. Nueva vida para {OwnerClientId}: {NetworkSalud.Value}");
        }
        else
        {
            Debug.LogWarning("Intento de QuitarVida desde un cliente no autorizado.");
        }
    }

    // Cuando cambie NetworkColor, ejecutar este metodo
    void ColorChanged(Color prevColor, Color newColor)
    {
        Debug.Log($"Color cambiado para {OwnerClientId} de {prevColor} a {newColor}");
        ChangeColor(newColor);
    }

    // // Funcion Move() y SubmitPositionRequestServerRpc() ya no son necesarias para la sincronización básica de NetworkTransform
    // public void Move()
    // {
    //     if (NetworkManager.Singleton.IsServer) { ... }
    //     else { ... }
    // }
    // [ServerRpc]
    // void SubmitPositionRequestServerRpc(ServerRpcParams rpcParams = default) { ... }
    // static Vector3 GetRandomPositionOnPlane() { ... }

    // Update se usa ahora para enviar input al servidor si somos el propietario
    void Update()
    {
        // NetworkTransform se encarga de sincronizar la posición. No hacemos transform.position = Position.Value;

        // Solo el propietario puede controlar su avatar
        if (!IsOwner) return;

        // --- NUEVO: Lógica de Movimiento basada en Input ---
        float horizontalInput = Input.GetAxis("Horizontal");
        float verticalInput = Input.GetAxis("Vertical");

        Vector3 moveDirection = new Vector3(horizontalInput, 0, verticalInput);
        // Normalizar para evitar movimiento más rápido en diagonal y aplicar velocidad y tiempo
        Vector3 movement = moveDirection.normalized * moveSpeed * Time.deltaTime;

        // Si hay movimiento, enviarlo al servidor
        if (movement != Vector3.zero)
        {
            // Directamente movemos el transform. NetworkTransform (con autoridad del Owner)
            // se encargará de enviar la información al servidor, y este la distribuirá.
            // NOTA: Si la autoridad fuese del servidor, necesitaríamos un ServerRpc.
            //       Como es del Owner, el movimiento local se replica.
            transform.Translate(movement, Space.World);

            // Si quisiéramos movimiento autorizado por servidor (como pide el enunciado):
            // SubmitMovementRequestServerRpc(movement);
        }
        // --- FIN Lógica de Movimiento ---
    }

    // --- NUEVO: ServerRpc para movimiento autorizado por servidor ---
    // Descomentar este bloque y comentar transform.Translate en Update() si se requiere autoridad del servidor estricta.
    /*
    [ServerRpc]
    void SubmitMovementRequestServerRpc(Vector3 movement)
    {
        if (IsServer)
        {
            // El servidor aplica el movimiento directamente al transform del objeto en el servidor
            // NetworkTransform (configurado como Server Authoritative en este caso) sincronizará a los clientes
            transform.Translate(movement, Space.World);
        }
    }
    */

    // --- NUEVO: Limpieza al destruir el objeto ---
    public override void OnNetworkDespawn()
    {
        // Desuscribirse de los eventos para evitar errores
        if (IsOwner)
        {
            if (NetworkSalud != null) NetworkSalud.OnValueChanged -= OnHealthChangedOwner;
        }
        else
        {
            if (NetworkSalud != null) NetworkSalud.OnValueChanged -= OnHealthChangedNonOwner;
        }
        if (!IsServer && NetworkColor != null)
        {
            NetworkColor.OnValueChanged -= ColorChanged;
        }

        // Destruir la barra de vida flotante si existe
        if (healthBarInstance != null)
        {
            Destroy(healthBarInstance);
        }

        base.OnNetworkDespawn();
    }
}
