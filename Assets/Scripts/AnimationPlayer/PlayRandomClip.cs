﻿using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Animation_Player {
    [Serializable]
    public class PlayRandomClip : AnimationState
    {
        public const string DefaultName = "New Random State";
        public List<AnimationClip> clips = new List<AnimationClip>();
        private int playedClip;

        private PlayRandomClip() { }

        public static PlayRandomClip Create(string name)
        {
            var state = new PlayRandomClip();
            state.Initialize(name, DefaultName);
            return state;
        }

        // Maybe I should be less of a dick and not write code like this?
        public override float Duration => (clips?.Count ?? 0) == 0 ? 0f : (clips[0]?.length ?? 0f); 
        public override bool Loops => (clips?.Count ?? 0) == 0 ? false : (clips[0]?.isLooping ?? false);

        public override Playable GeneratePlayable(PlayableGraph graph, Dictionary<string, List<BlendTreeController1D>> varTo1DBlendControllers, 
                                                  Dictionary<string, List<BlendTreeController2D>> varTo2DBlendControllers, Dictionary<string, float> blendVars)
        {
            playedClip = clips.GetRandomIdx();
            return GeneratePlayableFor(graph, playedClip);
        }

        private Playable GeneratePlayableFor(PlayableGraph graph, int clipIdx)
        {
            playedClip = clipIdx;
            var clip = clips[playedClip];
            AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, clip);
            clipPlayable.SetSpeed(speed);
            return clipPlayable;
        }

        public override void OnWillStartPlaying(PlayableGraph graph, AnimationMixerPlayable stateMixer, int ownIndex, ref Playable ownPlayable)
        {
            //this happens if we're looping, and were already partially playing when the state were started. In that case, don't snap to a different random choice. 
            if (ownPlayable.GetTime() > 0f)
                return;

            var wantedClip = clips.GetRandomIdx();
            if (wantedClip == playedClip)
                return;

            playedClip = wantedClip;
            var newPlayable = GeneratePlayableFor(graph, playedClip);
            var oldPlayable = ownPlayable;
            var oldWeight = stateMixer.GetInputWeight(ownIndex);

            var asClipPlayable = (AnimationClipPlayable) oldPlayable;
            asClipPlayable.SetAnimatedProperties(clips[wantedClip]);

            graph.Disconnect(stateMixer, ownIndex);
            stateMixer.ConnectInput(ownIndex, newPlayable, 0);
            stateMixer.SetInputWeight(ownIndex, oldWeight);

            oldPlayable.Destroy();
            ownPlayable = newPlayable;
        }
    }
}