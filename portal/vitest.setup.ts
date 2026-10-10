// Any setup scripts you might need go here

// jsdom shims for shadcn/Radix + sonner surfaces exercised by the account UI.
// jsdom omits these browser APIs, so component effects would otherwise throw.
if (typeof window !== 'undefined') {
  if (typeof window.matchMedia !== 'function') {
    window.matchMedia = (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList
  }

  if (typeof window.ResizeObserver !== 'function') {
    window.ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver
  }

  // The interview stage's voice aura pauses its shader when scrolled offscreen.
  // Nothing is ever visible in jsdom, so this only has to exist — the callback
  // never firing leaves the shader running, which is what the tests want.
  if (typeof window.IntersectionObserver !== 'function') {
    window.IntersectionObserver = class {
      readonly root = null
      readonly rootMargin = ''
      readonly thresholds: readonly number[] = []
      observe() {}
      unobserve() {}
      disconnect() {}
      takeRecords() {
        return []
      }
    } as unknown as typeof IntersectionObserver
    globalThis.IntersectionObserver = window.IntersectionObserver
  }

  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = function scrollIntoView() {}
  }
  if (!Element.prototype.hasPointerCapture) {
    Element.prototype.hasPointerCapture = function hasPointerCapture() {
      return false
    }
  }
  if (!Element.prototype.setPointerCapture) {
    Element.prototype.setPointerCapture = function setPointerCapture() {}
  }
  if (!Element.prototype.releasePointerCapture) {
    Element.prototype.releasePointerCapture = function releasePointerCapture() {}
  }

  // The shader component handles an unavailable WebGL context. Override the
  // jsdom placeholder so that exercising that fallback does not emit errors.
  HTMLCanvasElement.prototype.getContext = (() => null) as typeof HTMLCanvasElement.prototype.getContext
}
