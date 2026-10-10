internal static class BrandAssets
{
    /// <summary>The compact ERP Generator record mark. Sized via the .brand-mark CSS class.</summary>
    public const string MarkSvg =
        """
        <svg class="brand-mark" viewBox="0 0 64 64" role="img" aria-label="ERP Generator" xmlns="http://www.w3.org/2000/svg">
          <rect width="64" height="64" rx="14" fill="var(--primary)"/>
          <path d="M17 20h30M17 32h22M17 44h30" stroke="#fff" stroke-width="5" stroke-linecap="round"/>
        </svg>
        """;

    /// <summary>The ERP Generator wordmark, inlined so the runtime stays self-contained. Sized via the .auth-logo CSS class.</summary>
    public const string LogoSvg =
        """
        <svg class="auth-logo" role="img" aria-label="ERP Generator" viewBox="0 0 280 64" xmlns="http://www.w3.org/2000/svg">
          <rect x="2" y="10" width="44" height="44" rx="10" fill="var(--primary)"/>
          <path d="M14 23h20M14 32h15M14 41h20" stroke="#fff" stroke-width="4" stroke-linecap="round"/>
          <text x="58" y="41" fill="currentColor" font-family="Arial, sans-serif" font-size="27" font-weight="700">ERP Generator</text>
        </svg>
        """;
}
