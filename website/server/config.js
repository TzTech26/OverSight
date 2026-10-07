'use strict';
/**
 * Configuration: two sources merged.
 *  1. .env  — secrets / deploy-specific values (loaded by dotenv).
 *  2. data/settings.json — owner-editable settings changed from the /admin page
 *     (price, product copy, which channels are downloadable, domain, and
 *     optionally Stripe keys). Created with defaults on first run.
 *
 * For Stripe keys, an env value takes precedence over a settings.json value so
 * a deploy can force keys, but the admin UI can set them when env is blank.
 */
const path = require('path');
const fs = require('fs');
require('dotenv').config({ path: path.join(__dirname, '..', '.env') });

const ROOT = path.join(__dirname, '..');
const DATA_DIR = path.join(ROOT, 'data');
const RELEASES_CACHE = path.join(DATA_DIR, 'releases'); // local mirror of the NAS
const SETTINGS_PATH = path.join(DATA_DIR, 'settings.json');

fs.mkdirSync(DATA_DIR, { recursive: true });
fs.mkdirSync(RELEASES_CACHE, { recursive: true });

const DEFAULT_SETTINGS = {
  productName: 'Oversight',
  tagline: 'Precision zoom + auto-clicker for gamers. Zero overhead.',
  // Price in the smallest currency unit (cents). 1499 = $14.99.
  priceCents: 1499,
  currency: 'usd',
  // Which release channels buyers may download.
  //  verified = tested public builds; beta = "Work in progress" builds.
  channels: { verified: true, beta: true },
  // Default channel selected in the download dropdown.
  defaultChannel: 'verified',
  // Public site domain (blank until you buy one). Used for display only;
  // PUBLIC_URL in .env drives actual redirect URLs.
  domain: '',
  // Stripe keys settable from the admin UI (env overrides these if set).
  stripeSecretKey: '',
  stripePublishableKey: '',
  stripeWebhookSecret: ''
};

function loadSettings() {
  try {
    const raw = JSON.parse(fs.readFileSync(SETTINGS_PATH, 'utf8'));
    return { ...DEFAULT_SETTINGS, ...raw, channels: { ...DEFAULT_SETTINGS.channels, ...(raw.channels || {}) } };
  } catch {
    fs.writeFileSync(SETTINGS_PATH, JSON.stringify(DEFAULT_SETTINGS, null, 2));
    return { ...DEFAULT_SETTINGS };
  }
}

let settings = loadSettings();

function saveSettings(patch) {
  settings = { ...settings, ...patch, channels: { ...settings.channels, ...(patch.channels || {}) } };
  fs.writeFileSync(SETTINGS_PATH, JSON.stringify(settings, null, 2));
  return settings;
}

const env = process.env;

const config = {
  ROOT,
  DATA_DIR,
  RELEASES_CACHE,
  SETTINGS_PATH,
  port: parseInt(env.PORT || '3000', 10),
  host: env.HOST || '127.0.0.1',
  publicUrl: (env.PUBLIC_URL || `http://localhost:${env.PORT || 3000}`).replace(/\/+$/, ''),
  sessionSecret: env.SESSION_SECRET || 'insecure-dev-secret-change-me',
  adminEmails: (env.ADMIN_EMAILS || '')
    .split(',').map((s) => s.trim().toLowerCase()).filter(Boolean),

  nasReleasesDir: env.NAS_RELEASES_DIR || '',
  syncIntervalHours: parseInt(env.SYNC_INTERVAL_HOURS || '24', 10),

  google: {
    clientId: env.GOOGLE_CLIENT_ID || '',
    clientSecret: env.GOOGLE_CLIENT_SECRET || ''
  },

  smtp: {
    host: env.SMTP_HOST || '',
    port: parseInt(env.SMTP_PORT || '587', 10),
    user: env.SMTP_USER || '',
    pass: env.SMTP_PASS || '',
    from: env.MAIL_FROM || 'Oversight <no-reply@localhost>'
  },

  // Stripe getters read env first, then settings.json (admin-editable).
  get stripe() {
    return {
      secretKey: env.STRIPE_SECRET_KEY || settings.stripeSecretKey || '',
      publishableKey: env.STRIPE_PUBLISHABLE_KEY || settings.stripePublishableKey || '',
      webhookSecret: env.STRIPE_WEBHOOK_SECRET || settings.stripeWebhookSecret || ''
    };
  },

  get settings() { return settings; },
  saveSettings,
  reloadSettings() { settings = loadSettings(); return settings; },

  isGoogleConfigured() { return !!(this.google.clientId && this.google.clientSecret); },
  isSmtpConfigured() { return !!(this.smtp.host && this.smtp.user); },
  isStripeConfigured() { return !!this.stripe.secretKey; }
};

module.exports = config;
