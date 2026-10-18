# ── Paths ─────────────────────────────────────────────────────────────────────
DOCS    := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
SOURCE  := $(DOCS)/source
OUTPUT  := $(DOCS)/_build
VENV    := $(DOCS)/.venv
PYTHON  ?= python3

SPHINX  := $(VENV)/bin/sphinx-build
STAMP   := $(VENV)/.installed

# ── Default target ────────────────────────────────────────────────────────────
.PHONY: all
all: html

# ── Dependency checks ─────────────────────────────────────────────────────────
# Python 3.10+ creates the venv. Diagrams are Mermaid, rendered by the browser,
# so nothing else is needed.
.PHONY: check-python
check-python:
	@command -v $(PYTHON) >/dev/null 2>&1 || \
	    { echo "error: $(PYTHON) not found. Install Python 3.10+ or pass PYTHON=<path>."; exit 1; }
	@$(PYTHON) -c 'import sys; sys.exit(sys.version_info < (3, 10))' || \
	    { echo "error: Python 3.10+ required, found $$($(PYTHON) --version 2>&1)."; exit 1; }

# ── Virtual environment ───────────────────────────────────────────────────────
# Create the venv and install Sphinx on first use, or when requirements change.
# Debian/Ubuntu without python3-venv: the venv is created without pip, and pip
# is bootstrapped with get-pip.py.
$(STAMP): $(DOCS)/requirements.txt | check-python
	@$(PYTHON) -m venv $(VENV) 2>/dev/null || { \
	    echo "python3-venv missing: creating the venv without pip and bootstrapping pip"; \
	    rm -rf $(VENV); $(PYTHON) -m venv --without-pip $(VENV) && \
	    curl -sSL https://bootstrap.pypa.io/get-pip.py | $(VENV)/bin/python -q - ; }
	$(VENV)/bin/python -m pip install -q --upgrade pip
	$(VENV)/bin/python -m pip install -q -r $<
	touch $@

.PHONY: venv
venv: $(STAMP)

# ── HTML ──────────────────────────────────────────────────────────────────────
# -W turns warnings into errors so broken references fail the build.
.PHONY: html
html: $(STAMP)
	$(SPHINX) -b html -W --keep-going $(SOURCE) $(OUTPUT)/html
	@echo
	@echo "Open $(OUTPUT)/html/index.html"

# ── Link check ────────────────────────────────────────────────────────────────
.PHONY: linkcheck
linkcheck: $(STAMP)
	$(SPHINX) -b linkcheck $(SOURCE) $(OUTPUT)/linkcheck

# ── Clean ─────────────────────────────────────────────────────────────────────
.PHONY: clean
clean:
	rm -rf $(OUTPUT)

.PHONY: distclean
distclean: clean
	rm -rf $(VENV)
