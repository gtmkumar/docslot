#!/usr/bin/env node
// ============================================================================
// Design-token drift guard  (dependency-free — fs/path only, Node ESM)
//
// Canonical source of truth: docs/font-color.md. Every colour + font in the app
// is a CSS variable defined ONCE in src/styles/global.css (ported verbatim from
// font-color.md). Component code must reference those tokens — never inline a hex
// literal, a Tailwind arbitrary colour class, or a raw font stack. This script
// walks src/**/*.{ts,tsx} and exits 1 (printing every offending file:line) when,
// in NON-COMMENT code, it finds any of:
//   1. a hex colour literal used in a styling context,
//   2. a Tailwind arbitrary colour class  (text|bg|border|fill|stroke|ring|from|to|via)-[#...],
//   3. a fontFamily / font-family whose value does NOT reference var(--font-...).
//
// Wired as `npm run lint:tokens`. Zero-drift is enforced in CI via that script.
// ============================================================================

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const SRC_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'src');

// ── Allowlist — each entry is EXEMPT, with the reason it legitimately needs a
//    raw colour/font and therefore must never be treated as chrome drift. ────
const ALLOWLIST = [
  // Categorical DEMO department/doctor swatches: these hexes are DATA (chart
  // series / avatar tints), not UI chrome, so they cannot be design tokens.
  'lib/data.ts',
  // The <input type="color"> theme-picker DEFAULT accent/primary: the picker's
  // initial value legitimately needs concrete hex strings to seed the swatches.
  'stores/ui.ts',
];
const ALLOW_PREFIXES = [
  // Seed / fixture data (incl. the mock notifier's HTML email bodies). Not chrome.
  'lib/mock/',
];
/** A path is exempt if it is an allowlisted file, under an allowlisted dir, or an
 *  *.html email-template string literal (email clients can't load web fonts, so
 *  those templates ship a system-font stack + literal colours by necessity). */
function isAllowlisted(relPath) {
  const p = relPath.split(sep).join('/');
  if (p.endsWith('.html')) return true; // email templates — see note above
  if (ALLOWLIST.includes(p)) return true;
  return ALLOW_PREFIXES.some((prefix) => p.startsWith(prefix));
}

// ── Comment stripper ────────────────────────────────────────────────────────
// Blanks out // and /* */ comment characters (preserving newlines + column
// offsets) while leaving string-literal contents intact, so a `//` inside a URL
// string or a `#88a` issue-ref inside a comment never trips the checks below.
function stripComments(src) {
  const out = [];
  let i = 0;
  const n = src.length;
  let state = 'code'; // code | line | block | sq | dq | tpl
  while (i < n) {
    const c = src[i];
    const next = src[i + 1];
    if (state === 'code') {
      if (c === '/' && next === '/') { out.push('  '); i += 2; state = 'line'; continue; }
      if (c === '/' && next === '*') { out.push('  '); i += 2; state = 'block'; continue; }
      if (c === "'") { state = 'sq'; }
      else if (c === '"') { state = 'dq'; }
      else if (c === '`') { state = 'tpl'; }
      out.push(c); i += 1; continue;
    }
    if (state === 'line') {
      out.push(c === '\n' ? '\n' : ' '); if (c === '\n') state = 'code'; i += 1; continue;
    }
    if (state === 'block') {
      if (c === '*' && next === '/') { out.push('  '); i += 2; state = 'code'; continue; }
      out.push(c === '\n' ? '\n' : ' '); i += 1; continue;
    }
    // inside a string literal — copy verbatim, honour escapes, watch for the closer
    if (c === '\\') { out.push(c, next ?? ''); i += 2; continue; }
    if ((state === 'sq' && c === "'") || (state === 'dq' && c === '"') || (state === 'tpl' && c === '`')) {
      state = 'code';
    }
    out.push(c); i += 1;
  }
  return out.join('');
}

// ── Checks ────────────────────────────────────────────────────────────────
const HEX = /#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{4}|[0-9a-fA-F]{3})\b/g;
// A hex counts as "styling context" only if a CSS property / style cue precedes
// it in the same value expression — so #anchors, id="#abc123", etc. don't trip.
const STYLE_CUE =
  /(?:colou?r|background(?:-color)?|backgroundColor|\bbg\b|border(?:-[a-z]+)?|borderColor|fill|fillStyle|stroke|strokeStyle|stop-?color|stopColor|box-?shadow|boxShadow|shadow|outline|text-?shadow|textShadow|gradient|\brgba?\b|\bhsla?\b|caret-?color|caretColor|accent-?color|accentColor|--[\w-]+)\s*[:=]\s*['"`(]?[^;{}]*$/i;
const TW_ARBITRARY = /\b(?:text|bg|border|fill|stroke|ring|from|to|via)-\[#/g;
const FONT_FAMILY = /font-?family\s*[:=]\s*(['"`]?)([^'"`;}\n]+)/gi;

function scan(relPath, source) {
  const clean = stripComments(source);
  const lines = clean.split('\n');
  const violations = [];
  lines.forEach((line, idx) => {
    const lineNo = idx + 1;

    // 2) Tailwind arbitrary colour classes
    let m;
    TW_ARBITRARY.lastIndex = 0;
    while ((m = TW_ARBITRARY.exec(line))) {
      violations.push({ relPath, lineNo, kind: 'tailwind-arbitrary-colour', text: m[0] });
    }

    // 1) hex literal in a styling context
    HEX.lastIndex = 0;
    while ((m = HEX.exec(line))) {
      const pre = line.slice(0, m.index);
      // skip the ones already reported as Tailwind arbitrary colours
      if (/-\[$/.test(pre)) continue;
      if (STYLE_CUE.test(pre)) {
        violations.push({ relPath, lineNo, kind: 'hex-colour-literal', text: m[0] });
      }
    }

    // 3) font-family that does not reference a --font token
    FONT_FAMILY.lastIndex = 0;
    while ((m = FONT_FAMILY.exec(line))) {
      if (!/var\(--font/.test(m[2])) {
        violations.push({ relPath, lineNo, kind: 'non-token-font-family', text: m[0].trim() });
      }
    }
  });
  return violations;
}

// ── Walk src/**/*.{ts,tsx} ──────────────────────────────────────────────────
function walk(dir, acc) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (entry === 'node_modules') continue;
      walk(full, acc);
    } else if (/\.(ts|tsx)$/.test(entry)) {
      acc.push(full);
    }
  }
  return acc;
}

const files = walk(SRC_ROOT, []);
const allViolations = [];
for (const file of files) {
  const relPath = relative(SRC_ROOT, file);
  if (isAllowlisted(relPath)) continue;
  allViolations.push(...scan(relPath, readFileSync(file, 'utf8')));
}

if (allViolations.length > 0) {
  console.error(`\n✗ Design-token drift detected (${allViolations.length}) — see docs/font-color.md\n`);
  for (const v of allViolations) {
    console.error(`  src/${v.relPath}:${v.lineNo}  [${v.kind}]  ${v.text}`);
  }
  console.error('\nUse a CSS variable / Tailwind token instead of a raw hex, arbitrary colour class, or font stack.\n');
  process.exit(1);
}

console.log(`✓ lint:tokens — ${files.length} files clean, zero design-token drift.`);
