'use client'

import { useEffect, useState } from 'react'
import type { LocalAudioTrack } from 'livekit-client'

import { AgentAudioVisualizerAura } from '@/components/agents-ui/agent-audio-visualizer-aura'
import type { VoiceStatus } from './useVoiceDictation'

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
  const [color, setColor] = useState<`#${string}`>()
  useEffect(() => {
    // The visualizer needs a resolved color rather than a CSS variable.
    const primary = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim()
    if (primary.startsWith('#')) setColor(primary as `#${string}`)
  }, [])
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

  if (!color) return null

  return (
    <AgentAudioVisualizerAura
      size={size}
      state={state}
      color={color}
      audioTrack={track}
      className={className}
    />
  )
}
