﻿using System;
using System.Linq;
using NUnit.Framework.Constraints;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(AnimationPlayer))]
public class AnimationPlayerEditor : Editor
{
    private AnimationPlayer animationPlayer;

    private enum EditMode
    {
        States,
        Transitions
    }

    private PersistedInt selectedLayer;
    private PersistedInt selectedState; //for now in transition view
    private PersistedInt selectedToState;
    private PersistedEditMode selectedEditMode;

    private string[][] allStateNames;
    private bool shouldUpdateStateNames = true;

    private static bool stylesCreated = false;
    private static GUIStyle editLayerStyle;
    private static GUIStyle editLayerButton_Background;
    private static GUIStyle editLayerButton_NotSelected;
    private static GUIStyle editLayerButton_Selected;

    void OnEnable()
    {
        animationPlayer = (AnimationPlayer) target;
        animationPlayer.editTimeUpdateCallback -= Repaint;
        animationPlayer.editTimeUpdateCallback += Repaint;

        var instanceId = animationPlayer.GetInstanceID();

        selectedLayer = new PersistedInt(persistedLayer, instanceId);
        selectedEditMode = new PersistedEditMode(persistedEditMode, instanceId);
        selectedState = new PersistedInt(persistedState, instanceId);
        selectedToState = new PersistedInt(persistedToState, instanceId);
    }

    public override void OnInspectorGUI()
    {
        if (!stylesCreated)
        {
            var backgroundTex = EditorUtilities.MakeTex(1, 1, new Color(1.0f, 1.0f, 1.0f, .1f));
            editLayerStyle = new GUIStyle {normal = {background = backgroundTex}};

            var buttonBackgroundTex = EditorUtilities.MakeTex(1, 1, new Color(1.0f, 1.0f, 1.0f, 0.05f));
            var buttonSelectedTex = EditorUtilities.MakeTex(1, 1, new Color(1.0f, 1.0f, 1.0f, 0.05f));
            var buttonNotSelectedText = EditorUtilities.MakeTex(1, 1, new Color(1.0f, 1.0f, 1.0f, 0.2f));

            editLayerButton_Background = new GUIStyle {normal = {background = buttonBackgroundTex}};

            editLayerButton_NotSelected = new GUIStyle(GUI.skin.label)
            {
                normal = {background = buttonNotSelectedText}
            };

            editLayerButton_Selected = new GUIStyle(GUI.skin.label)
            {
                normal = {background = buttonSelectedTex}
            };

            stylesCreated = true;
        }

        if (shouldUpdateStateNames)
        {
            shouldUpdateStateNames = false;
            allStateNames = new string[animationPlayer.layers.Length][];
            for (int i = 0; i < animationPlayer.layers.Length; i++)
            {
                var states = animationPlayer.layers[i].states;
                allStateNames[i] = new string[states.Count];
                for (int j = 0; j < states.Count; j++)
                    allStateNames[i][j] = states[j].name;
            }
        }

        GUILayout.Space(30f);

        var numLayers = animationPlayer.layers.Length;
        if (animationPlayer.layers == null || numLayers == 0)
        {
            EditorGUILayout.LabelField("No layers in the animation player!");
            if (GUILayout.Button("Fix that!"))
            {
                animationPlayer.layers = new AnimationLayer[1];
                animationPlayer.layers[0] = AnimationLayer.CreateLayer();
            }
            return;
        }

        EditorUtilities.Splitter();

        selectedLayer.SetTo(DrawLayerSelection(numLayers));

        GUILayout.Space(10f);

        DrawSelectedLayer();

        EditorUtilities.Splitter();

        EditorGUILayout.LabelField("Default transition");

        Undo.RecordObject(animationPlayer, "Change default transition");
        animationPlayer.defaultTransition = DrawTransitionData(animationPlayer.defaultTransition);

        EditorUtilities.Splitter();

        DrawRuntimeDebugData();
    }

    private int DrawLayerSelection(int numLayers)
    {
        selectedLayer.SetTo(Mathf.Clamp(selectedLayer, 0, numLayers));

        EditorGUILayout.BeginHorizontal();

        GUILayout.FlexibleSpace();

        selectedLayer.SetTo(DrawLeftButton(selectedLayer));
        EditorGUILayout.LabelField("Selected layer: " + selectedLayer.Get(), GUILayout.Width(selectedLayerWidth));
        selectedLayer.SetTo(DrawRightButton(numLayers, selectedLayer));

        GUILayout.Space(10f);

        if (GUILayout.Button("Add layer", GUILayout.MinWidth(100f)))
        {
            Undo.RecordObject(animationPlayer, "Add layer to animation player");
            EditorUtilities.ExpandArrayByOne(ref animationPlayer.layers, AnimationLayer.CreateLayer);
            selectedLayer.SetTo(animationPlayer.layers.Length - 1);
            shouldUpdateStateNames = true;
        }
        if (GUILayout.Button("Delete layer", GUILayout.Width(100f)))
        {
            Undo.RecordObject(animationPlayer, "Delete layer from animation player");
            EditorUtilities.DeleteIndexFromArray(ref animationPlayer.layers, selectedLayer);
            selectedLayer.SetTo(Mathf.Max(0, selectedLayer - 1));
            shouldUpdateStateNames = true;
        }

        GUILayout.FlexibleSpace();

        EditorGUILayout.EndHorizontal();

        return selectedLayer;
    }

    private int DrawRightButton(int numLayers, int selectedLayer)
    {
        var disabled = numLayers == 1 || selectedLayer == numLayers - 1;

        EditorGUI.BeginDisabledGroup(disabled);
        if (GUILayout.Button(rightArrow, GUILayout.Width(arrowButtonWidth)))
            selectedLayer++;
        EditorGUI.EndDisabledGroup();
        return selectedLayer;
    }

    private int DrawLeftButton(int selectedLayer)
    {
        var disabled = selectedLayer == 0;
        EditorGUI.BeginDisabledGroup(disabled);
        if (GUILayout.Button(leftArrow, GUILayout.Width(arrowButtonWidth)))
            selectedLayer--;
        EditorGUI.EndDisabledGroup();
        return selectedLayer;
    }

    private void DrawSelectedLayer()
    {
        var layer = animationPlayer.layers[selectedLayer];

        layer.startWeight = EditorGUILayout.Slider("Layer Weight", layer.startWeight, 0f, 1f);
        layer.mask = EditorUtilities.ObjectField("Mask", layer.mask);

        GUILayout.Space(10f);

        EditorUtilities.Splitter();

        EditorGUILayout.BeginHorizontal(editLayerButton_Background);

        //HACK: Reseve the rects for later use
        GUILayout.Label("");
        var editStatesRect = GUILayoutUtility.GetLastRect();
        GUILayout.Label("");
        var editTransitionsRect = GUILayoutUtility.GetLastRect();
        GUILayout.Label("");
        var fooBarRect = GUILayoutUtility.GetLastRect();

        EditorGUILayout.EndHorizontal();

        //Hack part 2: expand the rects so they hit the next control
        Action<Rect, string, int> Draw = (rect, label, index) =>
        {
            rect.yMax += 2;
            rect.yMin -= 2;
            if (index == 0)
            {
                rect.x -= 4;
                rect.xMax += 4;
            }
            else if (index == 2)
            {
                rect.x += 4;
                rect.xMin -= 4;
            }

            var isSelected = index == selectedEditMode;
            var style = isSelected ? editLayerButton_Selected : editLayerButton_NotSelected;
            if (GUI.Button(rect, label, style))
                selectedEditMode.SetTo((EditMode) index);
        };

        Draw(editStatesRect, "Edit states", 0);
        Draw(editTransitionsRect, "Edit Transitions", 1);
        Draw(fooBarRect, "Foo/Bar: FooBar", 2);

        EditorGUILayout.BeginVertical(editLayerStyle);

        GUILayout.Space(10f);

        if (selectedEditMode == (int) EditMode.States)
            DrawStates();
        else if (selectedEditMode == (int) EditMode.Transitions)
            DrawTransitions();

        GUILayout.Space(20f);
        EditorGUILayout.EndVertical();
    }

    private void DrawStates()
    {
        var layer = animationPlayer.layers[selectedLayer];

        EditorGUILayout.LabelField("States:");

        EditorGUI.indentLevel++;
        int deleteIndex = -1;
        for (var i = 0; i < layer.states.Count; i++)
        {
            if (DrawState(layer.states[i]))
                deleteIndex = i;
            
            if(i != layer.states.Count - 1)
                GUILayout.Space(20f);
        }
        EditorGUI.indentLevel--;

        if (deleteIndex != -1)
        {
            Undo.RecordObject(animationPlayer, "Deleting state " + layer.states[deleteIndex].name);
            layer.states.RemoveAt(deleteIndex);
            layer.transitions.RemoveAll(transition => transition.fromState == deleteIndex || transition.toState == deleteIndex);
            foreach (var transition in layer.transitions)
            {
                //This would be so much better if transitions were placed on the state!
                if (transition.toState > deleteIndex)
                    transition.toState--;
                if (transition.fromState > deleteIndex)
                    transition.fromState--;
            }
            shouldUpdateStateNames = true;
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Add State"))
        {
            Undo.RecordObject(animationPlayer, "Add state to animation player");
            layer.states.Add(new AnimationState());
            shouldUpdateStateNames = true;
        }
    }

    private bool DrawState(AnimationState state)
    {
        const float labelWidth = 55f;
        
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Name", GUILayout.Width(labelWidth));
        state.name = EditorGUILayout.TextField(state.name);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Clip", GUILayout.Width(labelWidth));
        state.clip = EditorUtilities.ObjectField(state.clip);
        if (state.clip != null && string.IsNullOrEmpty(state.name))
            state.name = state.clip.name;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Speed", GUILayout.Width(labelWidth));
        state.speed = EditorGUILayout.DoubleField(state.speed);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(77f);
        bool delete = GUILayout.Button("delete");
        EditorGUILayout.EndHorizontal();
            
        return delete;
    }

    private void DrawTransitions()
    {
        var layer = animationPlayer.layers[selectedLayer];
        selectedState.SetTo(EditorGUILayout.Popup("Transitions from state", selectedState, allStateNames[selectedLayer]));
        
        EditorGUILayout.Space();

        EditorGUI.indentLevel++;
        selectedToState.SetTo(EditorGUILayout.Popup("Transtion to state", selectedToState, allStateNames[selectedLayer]));

        EditorGUILayout.Space();

        var transition = layer.transitions.Find(state => state.fromState == selectedState && state.toState == selectedToState);
        if (transition == null)
        {
            EditorGUILayout.LabelField("No transition defined!");
            if (GUILayout.Button("Create transition"))
            {
                Undo.RecordObject(animationPlayer, $"Add transition from {layer.states[selectedState].name} to {layer.states[selectedToState].name}");
                layer.transitions.Add(new StateTransition {fromState = selectedState, toState = selectedToState, transitionData = TransitionData.Linear(1f)});
            }
        }
        else
        {
            Undo.RecordObject(animationPlayer, $"Edit of transition from  {layer.states[selectedState].name} to {layer.states[selectedToState].name}");
            transition.transitionData = DrawTransitionData(transition.transitionData);
            
            if (GUILayout.Button("Clear transition"))
            {
                Undo.RecordObject(animationPlayer, $"Clear transition from  {layer.states[selectedState].name} to {layer.states[selectedToState].name}");
                layer.transitions.Remove(transition);
            }
        }


        EditorGUI.indentLevel--;
    }

    public TransitionData DrawTransitionData(TransitionData transitionData)
    {
        transitionData.type = (TransitionType) EditorGUILayout.EnumPopup("Type", transitionData.type);
        transitionData.duration = EditorGUILayout.FloatField("Duration", transitionData.duration);

        if (transitionData.type == TransitionType.Curve)
            transitionData.curve = EditorGUILayout.CurveField(transitionData.curve);

        return transitionData;

    }

    private void DrawRuntimeDebugData()
    {
        if (!Application.isPlaying)
            return;

        for (int i = animationPlayer.GetStateCount(selectedLayer) - 1; i >= 0; i--)
        {
            EditorGUILayout.BeginHorizontal();
            string stateName = animationPlayer.layers[selectedLayer].states[i].name;

            if (GUILayout.Button($"Blend to {stateName} using default transition"))
                animationPlayer.Play(i, selectedLayer);

            if (GUILayout.Button($"Blend to {stateName} over .5 secs"))
                animationPlayer.Play(i, TransitionData.Linear(.5f), selectedLayer);

            if (GUILayout.Button($"Snap to {stateName}"))
                animationPlayer.SnapTo(i, selectedLayer);

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.LabelField("Playing clip " + animationPlayer.GetCurrentPlayingClip(selectedLayer));
        for (int i = animationPlayer.GetStateCount() - 1; i >= 0; i--)
        {
            EditorGUILayout.LabelField("weigh for " + i + ": " + animationPlayer.GetClipWeight(i, selectedLayer));
        }
    }

    private const string rightArrow = "\u2192";
    private const string leftArrow = "\u2190";
    private const float arrowButtonWidth = 24f;
    private const float selectedLayerWidth = 108f;

    private const string persistedLayer = "APE_SelectedLayer_";
    private const string persistedState = "APE_SelectedState_";
    private const string persistedToState = "APE_SelectedToState_";
    private const string persistedEditMode = "APE_EditMode_";

    private class PersistedEditMode : PersistedVal<EditMode>
    {

        public PersistedEditMode(string key, int instanceID) : base(key, instanceID)
        { }

        protected override int ToInt(EditMode val)
        {
            return (int) val;
        }

        protected override EditMode ToType(int i)
        {
            return (EditMode) i;
        }
    }

}