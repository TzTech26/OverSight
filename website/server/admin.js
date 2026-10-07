'use strict';
/**
 * Owner admin console (/admin, admins only).
 *  - Trigger a NAS re-sync now, see sync status + manifest summary.
 *  - Set product name, tagline, price, currency, domain.
 *  - Toggle which channels buyers can download (verified / beta "work in progress").
 *  - Paste Stripe keys (stored in settings.json; env still wins if set).
 *  - View users + recent purchases; comp a free license; grant/revoke admin.
 */
const express = require('express');
const config = require('./config');
const db = require('./db');
const sync = require('./sync');
const { requireAdmin } = require('./middleware');

const router = express.Router();
router.use(requireAdmin);

router.get('/admin', (req, res) => {
  res.render('admin', {
    title: 'Admin',
    settings: config.settings,
    syncStatus: sync.getStatus(),
    manifest: sync.getManifest(),
    releases: sync.availableReleases(),
    users: db.allUsers(),
    purchases: db.recentPurchases(),
    stripe: config.stripe,
    stripeFromEnv: !!process.env.STRIPE_SECRET_KEY,
    googleConfigured: config.isGoogleConfigured(),
    smtpConfigured: config.isSmtpConfigured(),
    nasReleasesDir: config.nasReleasesDir,
    syncIntervalHours: config.syncIntervalHours,
    publicUrl: config.publicUrl,
    saved: req.query.saved || '',
    flash: req.query.flash || ''
  });
});

router.post('/admin/sync', (req, res) => {
  const st = sync.runSync();
  res.redirect(`/admin?flash=${encodeURIComponent(st.message)}`);
});

router.post('/admin/settings', (req, res) => {
  const b = req.body;
  config.saveSettings({
    productName: (b.productName || '').trim() || 'Oversight',
    tagline: (b.tagline || '').trim(),
    priceCents: Math.max(0, Math.round(parseFloat(b.priceDollars || '0') * 100)),
    currency: (b.currency || 'usd').trim().toLowerCase(),
    domain: (b.domain || '').trim(),
    defaultChannel: b.defaultChannel === 'beta' ? 'beta' : 'verified',
    channels: { verified: b.channelVerified === 'on', beta: b.channelBeta === 'on' }
  });
  res.redirect('/admin?saved=settings#settings');
});

router.post('/admin/stripe', (req, res) => {
  const b = req.body;
  config.saveSettings({
    stripeSecretKey: (b.stripeSecretKey || '').trim(),
    stripePublishableKey: (b.stripePublishableKey || '').trim(),
    stripeWebhookSecret: (b.stripeWebhookSecret || '').trim()
  });
  res.redirect('/admin?saved=stripe#stripe');
});

router.post('/admin/comp', (req, res) => {
  const user = db.getUserByEmail(req.body.email || '');
  if (user && !db.hasPaid(user.id)) db.compUser({ userId: user.id });
  res.redirect(`/admin?flash=${encodeURIComponent(user ? 'License granted to ' + user.email : 'No user with that email (they must sign in once first).')}#users`);
});

router.post('/admin/set-admin', (req, res) => {
  const user = db.getUserByEmail(req.body.email || '');
  if (user) db.setAdmin(user.id, req.body.on === '1');
  res.redirect('/admin?flash=Admin+updated#users');
});

module.exports = router;
