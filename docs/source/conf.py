# Sphinx configuration for the Relay Orbiter (KSP mod) documentation.

# ── Project ───────────────────────────────────────────────────────────────────
project   = "Relay Orbiter"
author    = "Alan Ocegueda"
copyright = "2026, Alan Ocegueda"
release   = "1.1.0"

# ── General ───────────────────────────────────────────────────────────────────
extensions = [
    "sphinx.ext.mathjax",
    "sphinxcontrib.mermaid",  # diagrams render in the browser: no Java or CLI needed
]

highlight_language = "none"  # plain `::` blocks are game text; code blocks name their language
exclude_patterns   = ["_build", ".venv"]

# ── HTML ──────────────────────────────────────────────────────────────────────
html_theme = "sphinx_rtd_theme"
html_title = "Relay Orbiter"
html_static_path = ["_static"]
html_css_files = ["custom.css"]
html_theme_options = {
    "navigation_depth": 3,
    "collapse_navigation": False,
}
