using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The TS Menu's "Align to QR" button and its status line: starts lining this headset up with the shared room
/// using the QR code on the floor (see <see cref="CoLocation"/>), and shows how it's going.
/// </summary>
public class AlignToQrButton : MonoBehaviour
{
    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Status;

    void Awake()
    {
        if (m_Button != null)
            m_Button.onClick.AddListener(OnPressed);
    }

    void OnPressed()
    {
        if (CoLocation.instance != null)
            CoLocation.instance.Align();
        else if (m_Status != null)
            m_Status.text = "Aligning isn't available in this scene.";
    }

    void Update()
    {
        if (m_Status != null && CoLocation.instance != null && m_Status.text != CoLocation.instance.status)
            m_Status.text = CoLocation.instance.status;
    }
}
