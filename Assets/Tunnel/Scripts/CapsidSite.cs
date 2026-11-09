using UnityEngine;

/// <summary>
/// One of a capsid's binding sites (see <see cref="CapsidSites"/>): its invisible trigger, which an antibody that
/// touches it recognises (see <see cref="AntibodyBinder"/>). Made by the capsid when it first needs its sites.
/// </summary>
public class CapsidSite : MonoBehaviour
{
    public CapsidSites capsid;
    public int index;
}
