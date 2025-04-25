using UnityEngine;
using Unity.Netcode;

[RequireComponent(typeof(NetworkObject))]
public class Player : NetworkBehaviour
{
    // Informacion que queremos sincronizar
    public NetworkVariable<Vector3> Position = new NetworkVariable<Vector3>();
    // En este caso, un color
    public NetworkVariable<Color> NetworkColor = new NetworkVariable<Color>();
    // 1º Variable que sincronnizar
    public NetworkVariable<int> NetworkSalud = new NetworkVariable<int>();

    private TMPro.TMP_Text textoVida;

    // Evento que se lanza cuando se hace spawn de un objeto
    // Hay que pensar que se lanza en cada instancia de cada cliente/servidor
    public override void OnNetworkSpawn()
    { 
        // Comprobamos si somos el propietario
        if (IsOwner)
        {
            Debug.Log(IsLocalPlayer);
            Move();

            // Busco la interfaz de vida
            var vidaPlayer = GameObject.Find("VidaPlayer");
            textoVida = vidaPlayer.GetComponent<TMPro.TMP_Text>();
            textoVida.text = NetworkSalud.Value.ToString();

            // 4º Suscribir callback si soy el propietario
            NetworkSalud.OnValueChanged += CambioVida;
        }
        if(!IsServer)
            NetworkColor.OnValueChanged += ColorChanged;
    }

    // Cambiar el color al MeshRenderer
    public void ChangeColor(Color color)
    {
        this.GetComponent<MeshRenderer>().material.color = color;
    }

    // 3º Metodo callback cuando cambia la vida
    public void CambioVida(int vidaAnterior, int nuevaVida)
    {
        Debug.Log("Vida went from " + vidaAnterior + " to " + nuevaVida);
        // Tiene que hacer el cambio que ve el cliente
        textoVida.text = nuevaVida.ToString();
    }

    // 2º Un metodo que cambie la variable
    public void QuitarVida(int cantidad)
    {
        if(IsServer)
            NetworkSalud.Value -= cantidad; // 6º Si esto lo hace el servidor, llama al callback en los clientes
    }

    // Cuando cambie NetworkColor, ejecutar este metodo
    void ColorChanged(Color prevColor, Color newColor)
    {
        Debug.Log("Color went from " + prevColor + " to " + newColor);
        ChangeColor(newColor);
    }

    // Funcion que mueve al jugador, solo puede ser ejecutada por el servidor
    public void Move()
    {
        // Si somos el servidor, movemos a nuestra instancia de jugador a una posicion aleatoria
        if (NetworkManager.Singleton.IsServer)
        {
            var randomPosition = GetRandomPositionOnPlane();
            transform.position = randomPosition;
            Position.Value = randomPosition;
        }
        // Si no somos el servidor, podemos pedir a este que haga el movimiento
        else
        {
            if(IsClient)
                SubmitPositionRequestServerRpc();
        }
    }

    [ServerRpc] // Solo el servidor puede ejecutar estos metodos
    void SubmitPositionRequestServerRpc(ServerRpcParams rpcParams = default)
    {
        if(IsServer)
            Position.Value = GetRandomPositionOnPlane();
    }

    // Metodo que devuelve una posicion aleatoria dentro de un rango en el plano XZ
    static Vector3 GetRandomPositionOnPlane()
    {
        return new Vector3(Random.Range(-3f, 3f), 1f, Random.Range(-3f, 3f));
    }

    // Actualizamos la posicion de esta instancia con la variable sincronizada
    void Update()
    {
        transform.position = Position.Value;
    }
}