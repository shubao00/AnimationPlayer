﻿using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Animation_Player
{

    [Serializable]
    public class AnimationLayer : ISerializationCallbackReceiver
    {
        //Serialized through ISerializationCallbackReceiver
        public List<AnimationState> states;
        public List<StateTransition> transitions;

        public string name;
        public float startWeight;
        public AvatarMask mask;
        public AnimationLayerType type = AnimationLayerType.Override;

        private PlayableGraph containingGraph;
        public AnimationMixerPlayable stateMixer { get; private set; }
        private int currentPlayedState;
        private bool firstFrame = true;
        private bool anyStatesHasAnimationEvents;
        private TransitionData defaultTransition;

        private AnimationLayerMixerPlayable layerMixer; //only use for re-initting!
        private int layerIndex; //only use for re-initting!

        //blend info:
        private bool transitioning;
        private TransitionData currentTransitionData;
        private float transitionStartTime;
        private List<bool> activeWhenBlendStarted;
        private List<float> valueWhenBlendStarted;
        private List<double> timeLastFrame;

        // transitionLookup[a, b] contains the index of the transition from a to b in transitions
        // transitionLookup[x, y] == -1 means that there is no transition defined between the states.
        private int[,] transitionLookup;
        private Playable[] runtimePlayables;

        private readonly Dictionary<string, int> stateNameToIdx = new Dictionary<string, int>(StringComparer.InvariantCulture);

        //@TODO: string key is slow
        private readonly Dictionary<string, float> blendVars = new Dictionary<string, float>();
        private readonly Dictionary<string, List<BlendTreeController1D>> varTo1DBlendControllers = new Dictionary<string, List<BlendTreeController1D>>();
        private readonly Dictionary<string, List<BlendTreeController2D>> varTo2DBlendControllers = new Dictionary<string, List<BlendTreeController2D>>();
        private readonly List<BlendTreeController2D> all2DControllers = new List<BlendTreeController2D>();

        private PlayAtTimeInstructionQueue playInstructionQueue;

        public void InitializeSelf(PlayableGraph graph, TransitionData defaultTransition)
        {
            this.defaultTransition = defaultTransition;
            playInstructionQueue = new PlayAtTimeInstructionQueue(this);

            containingGraph = graph;
            if (states.Count == 0)
            {
                stateMixer = AnimationMixerPlayable.Create(graph, 0, false);
                return;
            }

            foreach (var transition in transitions)
            {
                transition.FetchStates(states);
            }

            runtimePlayables = new Playable[states.Count];

            stateMixer = AnimationMixerPlayable.Create(graph, states.Count, false);
            stateMixer.SetInputWeight(0, 1f);
            currentPlayedState = 0;

            // Add the statess to the graph
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (state.animationEvents.Count > 0)
                    anyStatesHasAnimationEvents = true;

                stateNameToIdx[state.Name] = i;

                var playable = state.GeneratePlayable(graph, varTo1DBlendControllers, varTo2DBlendControllers, all2DControllers, blendVars);
                runtimePlayables[i] = playable;
                graph.Connect(playable, 0, stateMixer, i);
            }

            activeWhenBlendStarted = new List<bool>(states.Count);
            valueWhenBlendStarted = new List<float>(states.Count);
            timeLastFrame = new List<double>();
            for (int i = 0; i < states.Count; i++)
            {
                activeWhenBlendStarted.Add(false);
                valueWhenBlendStarted.Add(0f);
                timeLastFrame.Add(0f);
            }

            transitionLookup = new int[states.Count, states.Count];
            for (int i = 0; i < states.Count; i++)
            for (int j = 0; j < states.Count; j++)
                transitionLookup[i, j] = -1;

            for (var i = 0; i < transitions.Count; i++)
            {
                var transition = transitions[i];
                var fromState = states.IndexOf(transition.FromState);
                var toState = states.IndexOf(transition.ToState);
                if (fromState == -1 || toState == -1)
                {
                    //TODO: fixme
                }
                else
                {
                    if (transitionLookup[fromState, toState] != -1)
                        Debug.LogWarning("Found two transitions from " + states[fromState] + " to " + states[toState]);

                    transitionLookup[fromState, toState] = i;
                }
            }
        }

        public void InitializeLayerBlending(PlayableGraph graph, int layerIndex, AnimationLayerMixerPlayable layerMixer)
        {
            this.layerMixer = layerMixer;
            this.layerIndex = layerIndex;

            graph.Connect(stateMixer, 0, layerMixer, layerIndex);

            layerMixer.SetInputWeight(layerIndex, startWeight);
            layerMixer.SetLayerAdditive((uint) layerIndex, type == AnimationLayerType.Additive);
            if (mask != null)
                layerMixer.SetLayerMaskFromAvatarMask((uint) layerIndex, mask);

            if (layerIndex == 0)
            {
                //Doesn't make any sense for base layer to be additive!
                layerMixer.SetLayerAdditive((uint) layerIndex, false);
            }
            else
            {
                layerMixer.SetLayerAdditive((uint) layerIndex, type == AnimationLayerType.Additive);
            }
        }

        public bool HasState(string stateName)
        {
            foreach (var state in states)
            {
                if (state.Name == stateName)
                    return true;
            }

            return false;
        }

        public void Play(int state, TransitionData transition) {
            Play(state, FindCorrectTransition(state, transition), true);
        }

        private TransitionData FindCorrectTransition(int stateToPlay, TransitionData transition) {
            TransitionData transitionToUse;
            if (transition.type == TransitionType.UseDefined) {
                var transitionIndex = transitionLookup[currentPlayedState, stateToPlay];
                transitionToUse = transitionIndex == -1 ? defaultTransition : transitions[transitionIndex].transitionData;
            }
            else {
                transitionToUse = transition;
            }

            return transitionToUse;
        }

        private void Play(int newState, TransitionData transitionData, bool clearQueuedPlayInstructions)
        {
            if (clearQueuedPlayInstructions)
                playInstructionQueue.Clear();

            if (newState < 0 || newState >= states.Count)
            {
                Debug.LogError($"Trying to play out of bounds clip {newState}! There are {states.Count} clips in the animation player");
                return;
            }

            if (transitionData.type == TransitionType.Curve && transitionData.curve == null)
            {
                Debug.LogError("Trying to play an animationCurve based transition, but the transition curve is null!");
                return;
            }

            var currentWeightOfState = stateMixer.GetInputWeight(newState);
            var isCurrentlyPlaying = currentWeightOfState > 0f;

            if (!isCurrentlyPlaying)
            {
                runtimePlayables[newState].SetTime(0f);
                //Makes animation events set to time 0 play.
                timeLastFrame[newState] = -Time.deltaTime;
            }
            else if (!states[newState].Loops)
            {
                // We need to blend to a state currently playing, but since it's not looping, blending to a time different than 0 would look bad. 
                // So we do this:
                // Move the old version of the state to a new spot in the mixer, copy over the time and weight to that new spot.
                // Create a new version of the state at the old spot
                // Blend to the new state
                // Later, when the copy's weight hits zero, we discard it.

                var original = runtimePlayables[newState];
                var copy = states[newState].GeneratePlayable(containingGraph, varTo1DBlendControllers, varTo2DBlendControllers, all2DControllers, blendVars);
                var copyIndex = stateMixer.GetInputCount();
                stateMixer.SetInputCount(copyIndex + 1);

                containingGraph.Connect(copy, 0, stateMixer, copyIndex);

                activeWhenBlendStarted.Add(true);
                valueWhenBlendStarted.Add(currentWeightOfState);

                copy.SetTime(original.GetTime());

                stateMixer.SetInputWeight(copyIndex, currentWeightOfState);
                stateMixer.SetInputWeight(newState, 0);
            }

            states[newState].OnWillStartPlaying(containingGraph, stateMixer, newState, ref runtimePlayables[newState]);

            if (transitionData.duration <= 0f)
            {
                for (int i = 0; i < stateMixer.GetInputCount(); i++)
                {
                    stateMixer.SetInputWeight(i, i == newState ? 1f : 0f);
                }

                currentPlayedState = newState;
                transitioning = false;
            }
            else
            {
                for (int i = 0; i < stateMixer.GetInputCount(); i++)
                {
                    var currentMixVal = stateMixer.GetInputWeight(i);
                    activeWhenBlendStarted[i] = currentMixVal > 0f;
                    valueWhenBlendStarted[i] = currentMixVal;
                }

                transitioning = true;
                currentPlayedState = newState;
                currentTransitionData = transitionData;
                transitionStartTime = Time.time;
            }
        }


        public void QueuePlayCommand(int stateToPlay, QueueInstruction instruction, TransitionData transition)
        {
            playInstructionQueue.Enqueue(new PlayAtTimeInstruction(instruction, stateToPlay, transition));
        }

        public void ClearQueuedPlayCommands()
        {
            playInstructionQueue.Clear();
        }

        public void JumpToRelativeTime(float time) 
        {
            if (time > 1f) 
            {
                time = time % 1f;
            }
            else if (time < 0f) 
            {
                time = 1 - ((-time) % 1f);
            }

            for (int i = 0; i < stateMixer.GetInputCount(); i++) 
            {
                stateMixer.SetInputWeight(i, i == currentPlayedState ? 1f : 0f);
            }

            ClearFinishedTransitionStates();

            stateMixer.GetInput(currentPlayedState).SetTime(time * states[currentPlayedState].Duration);
        }

        public void Update()
        {
            if (states.Count == 0)
                return;
            if(anyStatesHasAnimationEvents)
                HandleAnimationEvents();
            HandleTransitions();
            HandleQueuedInstructions();
            for (int i = 0; i < all2DControllers.Count; i++) {
                all2DControllers[i].Update();
            }
            firstFrame = false;
        }

        private void HandleAnimationEvents()
        {
            for (int i = 0; i < states.Count; i++)
            {
                var time = stateMixer.GetInput(i).GetTime();
                if (currentPlayedState == i || stateMixer.GetInputWeight(i) > 0f)
                {
                    var currentTime = stateMixer.GetInput(i).GetTime();
                    states[i].HandleAnimationEvents(timeLastFrame[i], currentTime, stateMixer.GetInputWeight(i), firstFrame, currentPlayedState == i);
                }

                timeLastFrame[i] = time;
            }
        }

        private void HandleTransitions()
        {
            if (!transitioning)
                return;

            var lerpVal = (Time.time - transitionStartTime) / currentTransitionData.duration;
            if (currentTransitionData.type == TransitionType.Curve)
            {
                lerpVal = currentTransitionData.curve.Evaluate(lerpVal);
            }

            for (int i = 0; i < stateMixer.GetInputCount(); i++)
            {
                var isTargetClip = i == currentPlayedState;
                if (isTargetClip || activeWhenBlendStarted[i])
                {
                    var target = isTargetClip ? 1f : 0f;
                    stateMixer.SetInputWeight(i, Mathf.Lerp(valueWhenBlendStarted[i], target, lerpVal));
                }
            }

            var currentInputCount = stateMixer.GetInputCount();
            if (currentInputCount > states.Count) {
                ClearFinishedTransitionStates();
            }

            if (lerpVal >= 1)
            {
                transitioning = false;
                if (states.Count != stateMixer.GetInputCount())
                    throw new Exception($"{states.Count} != {stateMixer.GetInputCount()}");
            }
        }

        /// <summary>
        /// We generate some extra states at times to handle blending properly. This cleans out the ones of those that are done blending out.
        /// </summary>
        private void ClearFinishedTransitionStates() 
        {
            var currentInputCount = stateMixer.GetInputCount();
            for (int i = currentInputCount - 1; i >= states.Count; i--) 
            {
                if (stateMixer.GetInputWeight(i) < 0.01f) 
                {
                    activeWhenBlendStarted.RemoveAt(i);
                    valueWhenBlendStarted.RemoveAt(i);

                    var removedPlayable = stateMixer.GetInput(i);
                    removedPlayable.Destroy();

                    //Shift all excess playables one index down.
                    for (int j = i + 1; j < stateMixer.GetInputCount(); j++) 
                    {
                        var playable = stateMixer.GetInput(j);
                        var weight = stateMixer.GetInputWeight(j);
                        containingGraph.Disconnect(stateMixer, j);
                        containingGraph.Connect(playable, 0, stateMixer, j - 1);
                        stateMixer.SetInputWeight(j - 1, weight);
                    }

                    stateMixer.SetInputCount(stateMixer.GetInputCount() - 1);
                }
            }
        }

        private void HandleQueuedInstructions()
        {
            if (playInstructionQueue.Count > 0)
            {
                var instruction = playInstructionQueue.Peek();
                if (instruction.ShouldPlay()) {
                    Play(instruction.stateToPlay, FindCorrectTransition(instruction.stateToPlay, instruction.transition), false);
                    playInstructionQueue.Dequeue();
                }
            }
        }

        public float GetStateWeight(int state)
        {
            if (state < 0 || state >= states.Count)
            {
                Debug.LogError($"Trying to get the state weight for {state}, which is out of bounds! There are {states.Count} states!");
                return 0f;
            }

            return stateMixer.GetInputWeight(state);
        }

        public int GetStateIdx(string stateName)
        {
            if (stateNameToIdx.TryGetValue(stateName, out var idx))
                return idx;
            return -1;
        }

        public bool IsBlending()
        {
            return transitioning;
        }

        public static AnimationLayer CreateLayer()
        {
            var layer = new AnimationLayer
            {
                states = new List<AnimationState>(),
                transitions = new List<StateTransition>(),
                startWeight = 1f
            };
            return layer;
        }

        public void SetBlendVar(string var, float value)
        {
            blendVars[var] = value;

            List<BlendTreeController1D> blendControllers1D;
            if (varTo1DBlendControllers.TryGetValue(var, out blendControllers1D))
                foreach (var controller in blendControllers1D)
                    controller.SetValue(value);

            List<BlendTreeController2D> blendControllers2D;
            if (varTo2DBlendControllers.TryGetValue(var, out blendControllers2D))
                foreach (var controller in blendControllers2D)
                    controller.SetValue(var, value);
        }

        public float GetBlendVar(string var)
        {
            float result = 0f;
            blendVars.TryGetValue(var, out result);
            return result;
        }

        public void AddAllPlayingStatesTo(List<AnimationState> results)
        {
            results.Add(states[currentPlayedState]);

            for (var i = 0; i < states.Count; i++)
            {
                if (i == currentPlayedState)
                    return;
                var state = states[i];
                if (stateMixer.GetInputWeight(i) > 0f)
                {
                    results.Add(state);
                }
            }
        }

        public void AddAllPlayingStatesTo(List<int> results)
        {
            results.Add(currentPlayedState);

            for (var i = 0; i < states.Count; i++)
            {
                if (i == currentPlayedState)
                    return;
                var state = states[i];
                if (stateMixer.GetInputWeight(i) > 0f)
                {
                    results.Add(i);
                }
            }
        }

        public void AddAllPlayingStatesTo(List<string> results)
        {
            results.Add(states[currentPlayedState].Name);

            for (var i = 0; i < states.Count; i++)
            {
                if (i == currentPlayedState)
                    return;
                var state = states[i];
                if (stateMixer.GetInputWeight(i) > 0f)
                {
                    results.Add(state.Name);
                }
            }
        }

        public void AddTreesMatchingBlendVar(BlendVarController aggregateController, string blendVar)
        {
            List<BlendTreeController1D> blendControllers1D;
            if (varTo1DBlendControllers.TryGetValue(blendVar, out blendControllers1D))
                aggregateController.AddControllers(blendControllers1D);

            List<BlendTreeController2D> blendControllers2D;
            if (varTo2DBlendControllers.TryGetValue(blendVar, out blendControllers2D))
                aggregateController.AddControllers(blendControllers2D);
        }

        public AnimationState GetCurrentPlayingState()
        {
            if (states.Count == 0)
                return null;
            return states[currentPlayedState];
        }

        public int GetIndexOfPlayingState() {
            if (states.Count == 0)
                return -1;
            return currentPlayedState;
        }

        public void AddAllBlendVarsTo(List<string> result)
        {
            foreach (var key in blendVars.Keys)
            {
                result.Add(key);
            }
        }

        public bool HasBlendTreeUsingBlendVar(string blendVar)
        {
            return blendVars.Keys.Contains(blendVar);
        }

        public void SwapClipOnState(int state, AnimationClip clip, PlayableGraph graph) {
            var animationState = states[state];
            if (!(animationState is SingleClip)) {
                Debug.LogError($"Trying to swap the clip on the state {animationState.Name}, " +
                               $"but it is a {animationState.GetType().Name}! Only SingleClipState is supported");
            }

            var singleClipState = (SingleClip) animationState;
            singleClipState.clip = clip;
            var newPlayable = singleClipState.GeneratePlayable(graph, varTo1DBlendControllers, varTo2DBlendControllers, all2DControllers, blendVars);
            var currentPlayable = (AnimationClipPlayable) stateMixer.GetInput(state);

            var oldWeight = stateMixer.GetInputWeight(state);
            graph.Disconnect(stateMixer, state);
            currentPlayable.Destroy();
            stateMixer.ConnectInput(state, newPlayable, 0);
            stateMixer.SetInputWeight(state, oldWeight);
        }

        public int AddState(AnimationState state)
        {
            if (states.Count == 0) {
                HandleAddedFirstStateAfterStartup(state);
                return 0;
            }

            states.Add(state);
            if (state.animationEvents.Count > 0)
                anyStatesHasAnimationEvents = true;
            var playable = state.GeneratePlayable(containingGraph, varTo1DBlendControllers, varTo2DBlendControllers, all2DControllers, blendVars);

            var indexOfNew = states.Count - 1;
            stateNameToIdx[state.Name] = indexOfNew;

            Array.Resize(ref runtimePlayables, runtimePlayables.Length + 1);
            runtimePlayables[runtimePlayables.Length - 1] = playable;

            activeWhenBlendStarted.Add(false);
            valueWhenBlendStarted.Add(0f);
            timeLastFrame.Add(0d);

            var newLookup = new int[states.Count, states.Count];
            for (int i = 0; i < transitionLookup.GetLength(0); i++)
            for (int j = 0; j < transitionLookup.GetLength(1); j++)
            {
                newLookup[i, j] = transitionLookup[i, j];
            }

            for (int i = 0; i < states.Count; i++)
            {
                newLookup[i, indexOfNew] = -1;
                newLookup[indexOfNew, i] = -1;
            }

            transitionLookup = newLookup;
            
            stateMixer.SetInputCount(stateMixer.GetInputCount() + 1);

            //Shift all excess playables (ie blend helpers) one index up. Since their order doesn't count, could just swap the first one to the last index?
            for (int i = stateMixer.GetInputCount() - 1; i > states.Count; i--)
            {
                var p = stateMixer.GetInput(i);
                var weight = stateMixer.GetInputWeight(i);
                containingGraph.Disconnect(stateMixer, i - 1);
                containingGraph.Connect(p, 0, stateMixer, i);
                stateMixer.SetInputWeight(i, weight);
            }

            containingGraph.Connect(playable, 0, stateMixer, indexOfNew);
            return indexOfNew;
        }

        /// <summary>
        /// If the first state gets added after Initialize and InitializeLayerBlending has run, we disconnect and destroy the empty state mixer, and then
        /// re-initialize.
        /// </summary>
        private void HandleAddedFirstStateAfterStartup(AnimationState state)
        {
            states.Add(state);

            // layerMixer.IsValid => there's more than 1 layer.
            if(layerMixer.IsValid())
                containingGraph.Disconnect(layerMixer, layerIndex);

            stateMixer.Destroy();

            InitializeSelf(containingGraph, defaultTransition);

            if(layerMixer.IsValid())
                InitializeLayerBlending(containingGraph, layerIndex, layerMixer);
        }

#if UNITY_EDITOR
        public GUIContent[] layersForEditor;
#endif

        [SerializeField]
        private List<SingleClip> serializedSingleClipStates = new List<SingleClip>();
        [SerializeField]
        private List<BlendTree1D> serializedBlendTree1Ds = new List<BlendTree1D>();
        [SerializeField]
        private List<BlendTree2D> serializedBlendTree2Ds = new List<BlendTree2D>();
        [SerializeField]
        private List<PlayRandomClip> serializedSelectRandomStates = new List<PlayRandomClip>();
        [SerializeField]
        private SerializedGUID[] serializedStateOrder;
        public void OnBeforeSerialize()
        {
            if (serializedSingleClipStates == null)
                serializedSingleClipStates = new List<SingleClip>();
            else
                serializedSingleClipStates.Clear();

            if (serializedBlendTree1Ds == null)
                serializedBlendTree1Ds = new List<BlendTree1D>();
            else
                serializedBlendTree1Ds.Clear();

            if (serializedBlendTree2Ds == null)
                serializedBlendTree2Ds = new List<BlendTree2D>();
            else
                serializedBlendTree2Ds.Clear();
            if(serializedSelectRandomStates == null)
                serializedSelectRandomStates = new List<PlayRandomClip>();
            else 
                serializedSelectRandomStates.Clear();

            foreach (var state in states)
            {
                switch (state) {
                    case SingleClip singleClip:
                        serializedSingleClipStates.Add(singleClip);
                        continue;
                    case BlendTree1D blendTree1D:
                        serializedBlendTree1Ds.Add(blendTree1D);
                        continue;
                    case BlendTree2D blendTree2D:
                        serializedBlendTree2Ds.Add(blendTree2D);
                        continue;
                    case PlayRandomClip playRandomClip:
                        serializedSelectRandomStates.Add(playRandomClip);
                        continue;
                    default:
                        if (state != null)
                            Debug.LogError($"Found state in AnimationLayer's states that's of an unknown type, " +
                                           $"({state.GetType().Name})! Did you forget to implement the serialization?");
                        continue;
                }
            }

            serializedStateOrder = new SerializedGUID[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                serializedStateOrder[i] = states[i].GUID;
            }
        }

        public void OnAfterDeserialize()
        {
            if (states == null)
                states = new List<AnimationState>();
            else
                states.Clear();

            //AddRangde allocates. No, really!
            foreach (var state in serializedSingleClipStates)
                states.Add(state);

            foreach (var state in serializedBlendTree1Ds)
                states.Add(state);

            foreach (var state in serializedBlendTree2Ds)
                states.Add(state);

            foreach (var state in serializedSelectRandomStates)
                states.Add(state);

            serializedSingleClipStates.Clear();
            serializedBlendTree1Ds.Clear();
            serializedBlendTree2Ds.Clear();
            serializedSelectRandomStates.Clear();

            states.Sort(CompareListIndices);
        }

        private int CompareListIndices(AnimationState x, AnimationState y)
        {
            var xIndex = Array.IndexOf(serializedStateOrder, x.GUID);
            var yIndex = Array.IndexOf(serializedStateOrder, y.GUID);
            if (xIndex < yIndex)
                return -1;
            return xIndex > yIndex ? 1 : 0;
        }

        private struct PlayAtTimeInstruction
        {
            internal int stateToPlay;
            internal TransitionData transition;

            internal float isDoneTime;
            internal QueueStateType type;
            private bool boolParam;
            private float timeParam;

            internal bool CountFromQueued => boolParam;
            internal float Seconds => timeParam;
            internal float RelativeDuration => timeParam;

            public PlayAtTimeInstruction(QueueInstruction instruction, int stateToPlay, TransitionData transition)
            {
                type = instruction.type;
                boolParam = instruction.boolParam;
                timeParam = instruction.timeParam;

                this.stateToPlay = stateToPlay;
                this.transition = transition;

                if (instruction.type == QueueStateType.AfterSeconds && instruction.CountFromQueued)
                    isDoneTime = Time.time + instruction.timeParam;
                else
                    isDoneTime = -1; //gets set when moved to the top of the queue
            }

            public bool ShouldPlay() {
                var shouldPlay = Time.time > isDoneTime;
                if (shouldPlay)
                    return true;
                return shouldPlay;
            }
        }

        /// <summary>
        /// Ordered queue of play at time instructions
        /// </summary>
        private class PlayAtTimeInstructionQueue
        {
            public int Count { get; private set; }
            private const int bufferSizeIncrement = 10;
            private PlayAtTimeInstruction[] instructions = new PlayAtTimeInstruction[bufferSizeIncrement];
            private AnimationLayer animationLayer;

            public PlayAtTimeInstructionQueue(AnimationLayer animationLayer)
            {
                this.animationLayer = animationLayer;
            }

            public void Enqueue(PlayAtTimeInstruction instruction)
            {
                if (Count == instructions.Length)
                {
                    Array.Resize(ref instructions, instructions.Length + bufferSizeIncrement);
                }

                if(Count == 0)
                    instruction = MovedToTopOfQueue(instruction, animationLayer.currentPlayedState, animationLayer.stateMixer, animationLayer.states);

                instructions[Count] = instruction;
                Count++;
            }

            public PlayAtTimeInstruction Peek()
            {
                Debug.Assert(Count > 0, "Trying to peek play at time instructions, but there's no instructions!");
                return instructions[0];
            }

            public void Dequeue()
            {
                for (int i = 0; i < Count - 1; i++)
                {
                    instructions[i] = instructions[i + 1];
                }

                Count--;

                if (Count > 0)
                {
                    instructions[Count - 1] = MovedToTopOfQueue(instructions[Count - 1], animationLayer.currentPlayedState, animationLayer.stateMixer,
                                                                animationLayer.states);
                }
            }

            public void Clear()
            {
                Count = 0;
            }

            private PlayAtTimeInstruction MovedToTopOfQueue(PlayAtTimeInstruction playAtTime, int currentState, AnimationMixerPlayable stateMixer,
                                                            List<AnimationState> states) {
                switch (playAtTime.type) {
                    case QueueStateType.WhenCurrentDone: {
                        var duration = states[playAtTime.stateToPlay].Duration;
                        var playedTime = (float) stateMixer.GetInput(currentState).GetTime();

                        playAtTime.isDoneTime = Time.time + (duration - playedTime);
                        break;
                    }
                    case QueueStateType.AfterSeconds:
                        if (!playAtTime.CountFromQueued)
                        {
                            playAtTime.isDoneTime = Time.time + playAtTime.Seconds;
                        }
                        break;
                    case QueueStateType.BeforeCurrentDone_Seconds: {
                        var duration = states[playAtTime.stateToPlay].Duration;
                        var playedTime = (float) stateMixer.GetInput(currentState).GetTime();

                        var currentIsDoneTime = Time.time + (duration - playedTime);
                        playAtTime.isDoneTime = currentIsDoneTime - playAtTime.Seconds;
                        break;
                    }
                    case QueueStateType.BeforeCurrentDone_Relative: {
                        var duration = states[playAtTime.stateToPlay].Duration;
                        var playedTime = (float) stateMixer.GetInput(currentState).GetTime();

                        var currentIsDoneTime = Time.time + (duration - playedTime);
                        playAtTime.isDoneTime = currentIsDoneTime - (playAtTime.RelativeDuration * duration);
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return playAtTime;
            }
        }
    }
}