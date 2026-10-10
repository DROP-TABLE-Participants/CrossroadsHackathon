'use client'

import { useEffect, useState } from 'react'
import type { LocalAudioTrack } from 'livekit-client'

import { AgentAudioVisualizerAura } from '@/components/agents-ui/agent-audio-visualizer-aura'
import type { VoiceStatus } from './useVoiceDictation'

/** ERP Generator primary. The visualizer takes a literal hex, not a CSS variable. */
const ERP_GENERATOR_PURPLE = '#0f766e'

type AuraState = 'connecting' | 'listening' | 'thinking' | 'idle'

/**
 * The Aura visualizer, fed from our own microphone stream.
 *
 * The LiveKit component expects a LiveKit audio track, but we have no LiveKit
 * room — speech goes to ElevenLabs over our own socket. `LocalAudioTrack` wraps
 * a bare MediaStreamTrack without any room or server, so the shader gets real
 * amplitude while the transport stays entirely ours.
 */
export function VoiceAura({
  status,
  mediaStream,
  size = 'lg',
  className,
}: {
  status: VoiceStatus
  mediaStream: MediaStream | null
  size?: 'icon' | 'sm' | 'md' | 'lg' | 'xl'
  /** Merged over the size variant — lets the caller scale it responsively. */
  className?: string
}) {
  const [track, setTrack] = useState<LocalAudioTrack | undefined>()

  useEffect(() => {
    const audioTrack = mediaStream?.getAudioTracks()[0]
    if (!audioTrack) {
      setTrack(undefined)
      return
    }

    let cancelled = false
    // livekit-client is heavy and only needed once someone actually speaks —
    // load it on demand so the interview page stays light for people who type.
    void import('livekit-client').then(({ LocalAudioTrack }) => {
      if (cancelled) return
      setTrack(new LocalAudioTrack(audioTrack, undefined, false))
    })

    return () => {
      cancelled = true
      setTrack(undefined)
    }
  }, [mediaStream])

  const state: AuraState = status === 'connecting'
    ? 'connecting'
    : status === 'listening'
      ? 'listening'
      : 'idle'

  return (
    <AgentAudioVisualizerAura
      size={size}
      state={state}
      color={ERP_GENERATOR_PURPLE}
      audioTrack={track}
      className={className}
    />
  )
}
