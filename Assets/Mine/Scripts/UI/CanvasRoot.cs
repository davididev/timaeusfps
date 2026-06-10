using Unity.VisualScripting;
using UnityEngine;

/// <summary>
/// Utility class for enabling/disabling pages, also changing their size
/// </summary>
public class CanvasRoot : MonoBehaviour
{
    public static bool PausePressedTrigger = false;  //Called from PlayerMovement to avoid wasting memory with a copy of the Input Manager
    [System.Serializable]
    public struct IndivPage
    {
        public GameObject staticRoot;
        public GameObject dynamicRoot;
        [HideInInspector] public float target_scale, current_scale;

    }
    [SerializeField]
    public IndivPage[] pages;
    [SerializeField] AudioClip switchWindowSound, unpauseWindowSound;
    const float SCALE_PER_SECOND = 2.0f;
    const float DISABLE_ME_SCALE = 0.001f;
    int current_page_id = -1;

    //Initilize the pages
    void Awake()
    {
        for (int i = 0; i < pages.Length; i++)
        {
            if (i > 0)
            {
                pages[i].current_scale = DISABLE_ME_SCALE;
                pages[i].target_scale = DISABLE_ME_SCALE;
                pages[i].dynamicRoot.transform.localScale = DISABLE_ME_SCALE * Vector3.one;
                pages[i].staticRoot.transform.localScale = DISABLE_ME_SCALE * Vector3.one;
            }

        }
    }

    private void Start()
    {
        SetPageID(0, true);
        
    }

    public void SetPageID(int pageID, bool supressSound = false)
    {
        if (supressSound == false)
        {
            if(pageID == 0)
                SoundFXPlayer.PlaySound(unpauseWindowSound, transform.position, 100.0f); 
            else
            {
                float pitch = 0.8f + (pageID * 0.2f);
                SoundFXPlayer.PlaySound(switchWindowSound, transform.position, 100.0f, 1f, 1f, pitch);
            }
                
                  
        }
            
        current_page_id = pageID;
        if (pageID == 0)
        {
            Cursor.lockState = CursorLockMode.Locked;  //Not paused, lock the mouse
            Time.timeScale = 1.0f;
        }
        else  //Any other page means the game is paused
        {
            Cursor.lockState = CursorLockMode.None;  //paused, unlock the mouse
            Time.timeScale = 0.0f;
        }


        for(int i = 0; i < pages.Length; i++)
        {
            
            if (i == pageID)
                pages[i].target_scale = 1.0f;
            else
                pages[i].target_scale = DISABLE_ME_SCALE;
        }
    }

    // Update is called once per frame
    void Update()
    {
        if(PausePressedTrigger == true)
        {
            PausePressedTrigger = false;
            if (current_page_id == 0)
                SetPageID(1);  //Open up the default pause menu;
            else
                SetPageID(0);  //Unpause the game
        }
        for (int i = 0; i < pages.Length; i++)
        {
            if (!Mathf.Approximately(pages[i].current_scale, pages[i].target_scale))
            {
                pages[i].current_scale = Mathf.MoveTowards(pages[i].current_scale, pages[i].target_scale, SCALE_PER_SECOND * Time.unscaledDeltaTime);
                pages[i].staticRoot.transform.localScale = Vector3.one * pages[i].current_scale;
                pages[i].dynamicRoot.transform.localScale = Vector3.one * pages[i].current_scale;

                bool is_inactive = Mathf.Approximately(pages[i].current_scale, DISABLE_ME_SCALE);
                pages[i].staticRoot.SetActive(!is_inactive);
                pages[i].dynamicRoot.SetActive(!is_inactive);

                
            }
            
        }
    }
}
