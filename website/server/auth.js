'use strict';
/**
 * Authentication routes.
 *  - Google Sign-In (OAuth 2.0 authorization-code flow via google-auth-library)
 *  - Email magic-link (tokenized one-time login URL, emailed or logged)
 * A successful login sets req.session.userId.
 */
const express = require('express');
const rateLimit = require('express-rate-limit');
const { OAuth2Client } = require('google-auth-library');

const config = require('./config');
const db = require('./db');
const { sendMail } = require('./mailer');

const router = express.Router();

const loginLimiter = rateLimit({ windowMs: 15 * 60 * 1000, max: 20, standardHeaders: true, legacyHeaders: false });

function googleClient() {
  return new OAuth2Client(
    config.google.clientId,
    config.google.clientSecret,
    `${config.publicUrl}/auth/google/callback`
  );
}

function finishLogin(req, res, user, nextUrl) {
  req.session.userId = user.id;
  const dest = safeNext(nextUrl);
  return res.redirect(dest);
}

// Only allow same-site relative redirects to avoid open-redirects.
function safeNext(next) {
  if (typeof next === 'string' && next.startsWith('/') && !next.startsWith('//')) return next;
  return '/download';
}

// --- Login page -------------------------------------------------------------
router.get('/login', (req, res) => {
  if (req.user) return res.redirect(safeNext(req.query.next));
  res.render('login', {
    title: 'Sign in',
    next: typeof req.query.next === 'string' ? req.query.next : '',
    googleEnabled: config.isGoogleConfigured(),
    sent: req.query.sent === '1',
    error: req.query.error || ''
  });
});

router.get('/logout', (req, res) => {
  req.session.destroy(() => res.redirect('/'));
});

// --- Google OAuth -----------------------------------------------------------
router.get('/auth/google', (req, res) => {
  if (!config.isGoogleConfigured()) return res.redirect('/login?error=Google+sign-in+is+not+configured+yet');
  const url = googleClient().generateAuthUrl({
    access_type: 'online',
    scope: ['openid', 'email', 'profile'],
    state: safeNext(req.query.next),
    prompt: 'select_account'
  });
  res.redirect(url);
});

router.get('/auth/google/callback', async (req, res) => {
  try {
    const client = googleClient();
    const { tokens } = await client.getToken(req.query.code);
    const ticket = await client.verifyIdToken({ idToken: tokens.id_token, audience: config.google.clientId });
    const p = ticket.getPayload();
    if (!p || !p.email || !p.email_verified) throw new Error('Google account email not verified');
    const user = db.upsertUser({ email: p.email, name: p.name, googleId: p.sub }, config.adminEmails);
    return finishLogin(req, res, user, req.query.state);
  } catch (err) {
    console.error('[auth] google callback failed:', err.message);
    return res.redirect('/login?error=Google+sign-in+failed');
  }
});

// --- Email magic link -------------------------------------------------------
router.post('/auth/email', loginLimiter, async (req, res) => {
  const email = String(req.body.email || '').trim().toLowerCase();
  const next = safeNext(req.body.next);
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) {
    return res.redirect(`/login?error=Enter+a+valid+email&next=${encodeURIComponent(next)}`);
  }
  const token = db.createLoginToken(email);
  const link = `${config.publicUrl}/auth/email/verify?token=${token}&next=${encodeURIComponent(next)}`;
  await sendMail({
    to: email,
    subject: `Your ${config.settings.productName} sign-in link`,
    text: `Click to sign in (valid 15 minutes):\n\n${link}\n\nIf you didn't request this, ignore this email.`,
    html: `<p>Click to sign in to <b>${config.settings.productName}</b> (valid 15 minutes):</p>
           <p><a href="${link}">Sign in</a></p><p style="color:#888">If you didn't request this, ignore this email.</p>`
  });
  console.log(`[auth] magic link for ${email}: ${link}`);
  res.redirect(`/login?sent=1&next=${encodeURIComponent(next)}`);
});

router.get('/auth/email/verify', (req, res) => {
  const email = db.consumeLoginToken(String(req.query.token || ''));
  if (!email) return res.redirect('/login?error=That+link+expired.+Request+a+new+one');
  const user = db.upsertUser({ email }, config.adminEmails);
  return finishLogin(req, res, user, req.query.next);
});

module.exports = router;
