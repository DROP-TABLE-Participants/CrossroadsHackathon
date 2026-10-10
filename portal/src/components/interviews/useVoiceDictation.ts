'use client'

import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * Live speech-to-text for the interview answer box.
 *
 * The participant speaks, words appear as they go, and they can still edit
 * before sending — nothing is submitted on their behalf. The account API key is
 * never here: the browser asks our own route for a short-lived single-use token
 * and connects with that.
 *
 * Protocol (verified against the live socket): audio goes up as base64 PCM
 * 16-bit mono @16kHz inside JSON `input_audio_chunk` messages; interim text
 * arrives as `partial_transcript` and settled text as `committed_transcript`
 * (separate event types — there is no is_final flag).
 */

const SAMPLE_RATE = 16000
/** ~128ms of audio per message; matches ElevenLabs' own chunking guidance. */
const CHUNK_SAMPLES = 2048
/**
 * Seconds of silence that end an utterance. ElevenLabs' own voice-activity
 * detection decides this server-side — it knows speech from room noise far
 * better than a timer over microphone amplitude would, and it means the mic can
 * sit open without transcribing a quiet workshop.
 */
const SILENCE_SECONDS = 2.5

export type VoiceStatus = 'idle' | 'connecting' | 'listening' | 'unsupported' | 'denied' | 'error'

type VoiceDictationOptions = {
  interviewToken: string
  languageCode: string
  /** Receives text that has settled — appended to the answer box. */
  onCommitted: (text: string) => void
}

function floatToPcm16(input: Float32Array): ArrayBuffer {
  const output = new Int16Array(input.length)
  for (let i = 0; i < input.length; i += 1) {
    const clamped = Math.max(-1, Math.min(1, input[i]))
    output[i] = clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff
  }
  return output.buffer
}

function toBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  // Chunked to avoid blowing the argument limit on large buffers.
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  }
  return btoa(binary)
}

export function useVoiceDictation({ interviewToken, languageCode, onCommitted }: VoiceDictationOptions) {
  const [status, setStatus] = useState<VoiceStatus>('idle')
  /** Interim words, shown greyed until they settle. */
  const [interimText, setInterimText] = useState('')
  /** Exposed so a visualizer can read the live mic amplitude. */
  const [mediaStream, setMediaStream] = useState<MediaStream | null>(null)

  const socketRef = useRef<WebSocket | null>(null)
  const streamRef = useRef<MediaStream | null>(null)
  const audioContextRef = useRef<AudioContext | null>(null)
  const processorRef = useRef<ScriptProcessorNode | null>(null)
  // Held in a ref so a caller passing an inline callback cannot tear the socket
  // down mid-sentence.
  const onCommittedRef = useRef(onCommitted)
  useEffect(() => {
    onCommittedRef.current = onCommitted
  }, [onCommitted])

  const teardown = useCallback(() => {
    processorRef.current?.disconnect()
    processorRef.current = null
    streamRef.current?.getTracks().forEach((track) => track.stop())
    streamRef.current = null
    setMediaStream(null)
    audioContextRef.current?.close().catch(() => {})
    audioContextRef.current = null
    const socket = socketRef.current
    socketRef.current = null
    if (socket && socket.readyState === WebSocket.OPEN) {
      // Flush whatever is buffered so the last words are not lost, then close.
      try {
        socket.send(JSON.stringify({
          message_type: 'input_audio_chunk',
          audio_base_64: '',
          sample_rate: SAMPLE_RATE,
          commit: true,
        }))
      } catch {
        // Socket already gone — nothing to flush.
      }
      setTimeout(() => socket.close(), 400)
    }
    setInterimText('')
  }, [])

  const stop = useCallback(() => {
    teardown()
    setStatus('idle')
  }, [teardown])

  const start = useCallback(async () => {
    if (typeof window === 'undefined' || !navigator.mediaDevices?.getUserMedia || !window.AudioContext) {
      setStatus('unsupported')
      return
    }

    setStatus('connecting')
    try {
      const response = await fetch(`/api/interviews/${encodeURIComponent(interviewToken)}/voice-token`, {
        method: 'POST',
      })
      if (!response.ok) {
        setStatus('error')
        return
      }
      const { token, host, modelId } = await response.json()
      if (!token) {
        setStatus('error')
        return
      }

      let stream: MediaStream
      try {
        stream = await navigator.mediaDevices.getUserMedia({
          audio: { echoCancellation: true, noiseSuppression: true, channelCount: 1 },
        })
      } catch {
        // Almost always a declined permission prompt; treat anything else the
        // same way — the participant just needs the "allow the microphone" copy.
        setStatus('denied')
        return
      }
      streamRef.current = stream
      setMediaStream(stream)

      // `commit_strategy=vad` lets the server close each utterance itself after
      // SILENCE_SECONDS of quiet, so the participant just talks and stops —
      // no button to end a sentence, and no transcript while nobody speaks.
      const url = `wss://${host}/v1/speech-to-text/realtime` +
        `?model_id=${encodeURIComponent(modelId)}` +
        `&language_code=${encodeURIComponent(languageCode)}` +
        `&commit_strategy=vad` +
        `&vad_silence_threshold_secs=${SILENCE_SECONDS}` +
        `&token=${encodeURIComponent(token)}`
      const socket = new WebSocket(url)
      socketRef.current = socket

      socket.onopen = () => {
        const context = new AudioContext({ sampleRate: SAMPLE_RATE })
        audioContextRef.current = context
        const source = context.createMediaStreamSource(stream)
        // ScriptProcessor is deprecated but is the only mic-capture path that
        // works without shipping a separate AudioWorklet file; the buffer size
        // is small enough that the main-thread cost is not noticeable here.
        const processor = context.createScriptProcessor(CHUNK_SAMPLES, 1, 1)
        processorRef.current = processor

        processor.onaudioprocess = (event) => {
          if (socket.readyState !== WebSocket.OPEN) return
          const channel = event.inputBuffer.getChannelData(0)
          socket.send(JSON.stringify({
            message_type: 'input_audio_chunk',
            audio_base_64: toBase64(floatToPcm16(channel)),
            sample_rate: SAMPLE_RATE,
            commit: false,
          }))
        }

        source.connect(processor)
        processor.connect(context.destination)
        setStatus('listening')
      }

      socket.onmessage = (event) => {
        let message: Record<string, unknown>
        try {
          message = JSON.parse(event.data)
        } catch {
          return
        }

        const type = message.message_type
        if (type === 'partial_transcript') {
          setInterimText(typeof message.text === 'string' ? message.text : '')
          return
        }
        if (type === 'committed_transcript') {
          const text = typeof message.text === 'string' ? message.text.trim() : ''
          if (text) onCommittedRef.current(text)
          setInterimText('')
          return
        }
        if (typeof type === 'string' && type.includes('error')) {
          teardown()
          setStatus('error')
        }
      }

      socket.onerror = () => {
        teardown()
        setStatus('error')
      }
      socket.onclose = () => {
        // A close we did not initiate (token expiry, session cap) should drop
        // the UI out of "listening" rather than leave a dead mic button on.
        if (socketRef.current === socket) {
          teardown()
          setStatus('idle')
        }
      }
    } catch {
      teardown()
      setStatus('error')
    }
  }, [interviewToken, languageCode, teardown])

  useEffect(() => teardown, [teardown])

  return {
    status,
    interimText,
    mediaStream,
    start,
    stop,
    listening: status === 'listening' || status === 'connecting',
  }
}
