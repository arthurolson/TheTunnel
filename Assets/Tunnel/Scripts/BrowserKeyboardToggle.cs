using TLab.VKeyborad;
using UnityEngine;

/// <summary>
/// The browser panel's Keyboard button. Shows or hides the virtual keyboard; when showing it and nothing is
/// receiving keys yet, the web page gets them, so typing always lands somewhere.
/// </summary>
public class BrowserKeyboardToggle : MonoBehaviour
{
    [SerializeField]
    BaseVKeyborad m_Keyboard;

    [SerializeField]
    [Tooltip("Receives keys when the keyboard is opened with no field selected (the web page).")]
    BaseInputField m_DefaultField;

    public void Toggle()
    {
        if (m_Keyboard == null)
            return;

        if (m_Keyboard.isVisible)
        {
            m_Keyboard.SetVisibility(false);
            return;
        }

        if (m_Keyboard.inputFieldBase == null && m_DefaultField != null)
            m_DefaultField.SwitchInputField(true);
        m_Keyboard.SetVisibility(true);
    }
}
