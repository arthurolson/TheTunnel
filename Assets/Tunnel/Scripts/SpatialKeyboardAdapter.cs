using TLab.VKeyborad;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// Lets the XRI Spatial Keyboard stand in for TLab's virtual keyboard. TLab input fields (the web page, the
/// address bar) switch focus and show/hide the keyboard through this exactly as before, and every edit made on
/// the spatial keyboard is replayed into the focused field as key strokes.
/// </summary>
public class SpatialKeyboardAdapter : BaseVKeyborad
{
    [SerializeField]
    XRKeyboard m_Keyboard;

    // What the focused field has been sent since the last reset; the keyboard's own text is diffed against it.
    string m_Sent = string.Empty;
    bool m_Suppress;
    bool m_BackspaceSent;

    public override bool isVisible => m_Keyboard != null && m_Keyboard.isOpen;

    void OnEnable()
    {
        if (m_Keyboard == null)
            return;
        m_Keyboard.onTextUpdated.AddListener(OnTextUpdated);
        m_Keyboard.onTextSubmitted.AddListener(OnTextSubmitted);
        m_Keyboard.onKeyPressed.AddListener(OnKeyPressed);
    }

    void OnDisable()
    {
        if (m_Keyboard == null)
            return;
        m_Keyboard.onTextUpdated.RemoveListener(OnTextUpdated);
        m_Keyboard.onTextSubmitted.RemoveListener(OnTextSubmitted);
        m_Keyboard.onKeyPressed.RemoveListener(OnKeyPressed);
    }

    public override void SetVisibility(bool active)
    {
        if (m_Keyboard == null || active == isVisible)
            return;

        m_Suppress = true;
        if (active)
            m_Keyboard.OpenCleared();
        else
            m_Keyboard.Close(true);
        m_Suppress = false;
        m_Sent = string.Empty;

        m_onVisibilityChanged?.Invoke(active);
    }

    public override void SwitchInputField(BaseInputField inputFieldBase)
    {
        base.SwitchInputField(inputFieldBase);
        ResetBuffer();
    }

    void ResetBuffer()
    {
        m_Sent = string.Empty;
        if (m_Keyboard == null || !m_Keyboard.isOpen)
            return;
        m_Suppress = true;
        m_Keyboard.Clear();
        m_Suppress = false;
    }

    void OnTextUpdated(KeyboardTextEventArgs args)
    {
        if (m_Suppress)
            return;

        var field = m_inputField;
        string now = args.keyboardText ?? string.Empty;

        int common = 0;
        while (common < m_Sent.Length && common < now.Length && m_Sent[common] == now[common])
            common++;

        if (field != null)
        {
            for (int i = common; i < m_Sent.Length; i++)
            {
                field.OnBackSpaceKey();
                m_BackspaceSent = true;
            }

            for (int i = common; i < now.Length; i++)
            {
                char c = now[i];
                if (c == '\n')
                    field.OnEnterKey();
                else if (c == ' ')
                    field.OnSpaceKey();
                else
                    field.OnKey(c.ToString());
            }
        }

        m_Sent = now;
    }

    void OnKeyPressed(KeyboardKeyEventArgs args)
    {
        // Backspace with nothing left in the keyboard's own buffer still deletes text already in the field.
        var key = args.key;
        bool isBackspace = key != null && (key.keyCode == KeyCode.Backspace || key.GetEffectiveCharacter() == "\\b");
        if (isBackspace && !m_BackspaceSent && m_inputField != null)
            m_inputField.OnBackSpaceKey();
        m_BackspaceSent = false;
    }

    void OnTextSubmitted(KeyboardTextEventArgs args)
    {
        if (m_inputField != null)
            m_inputField.OnEnterKey();
        ResetBuffer();
    }
}
