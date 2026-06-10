using System;
using UnityEngine;
using UnityEngine.EventSystems;

public class UI_PauseMenuMain : MonoBehaviour
{
    [SerializeField] GameObject first_button;
    [SerializeField] CanvasRoot root;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    private void OnEnable()
    {
        EventSystem.current.SetSelectedGameObject(first_button);  //Focus the UI on the first button
    }

    public void ResumeGameButton()
    {
        root.SetPageID(0);
    }

    public void SettingsButton()
    {
        Debug.Log("TODO: Create settings button");
    }

    public void QuitButton()
    {
        Application.Quit();
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
