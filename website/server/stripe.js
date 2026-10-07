'use strict';
/**
 * Stripe one-time purchase.
 *  - POST /buy            -> creates a Checkout Session, redirects to Stripe.
 *  - GET  /buy/success    -> fallback reconcile (retrieves the session) so a
 *                            purchase lands even before the webhook is set up.
 *  - POST /stripe/webhook -> authoritative reconcile (checkout.session.completed).
 *
 * The webhook needs the RAW request body, so it's exported separately and
 * mounted in index.js BEFORE the JSON body parser.
 */
const express = require('express');
const Stripe = require('stripe');

const config = require('./config');
const db = require('./db');
const { requireAuth } = require('./middleware');

function client() {
  const key = config.stripe.secretKey;
  if (!key) return null;
  return new Stripe(key, { apiVersion: '2024-12-18.acacia' });
}

/** Mark a session paid + create/flip the purchase row. Idempotent. */
function fulfill(session) {
  const userId = parseInt(session.client_reference_id || (session.metadata && session.metadata.userId), 10);
  if (!userId) return;
  const existing = db.purchaseBySession(session.id);
  if (existing) {
    if (existing.status !== 'paid') db.markSessionPaid(session.id, session.payment_intent);
    return;
  }
  db.createPendingPurchase({
    userId,
    sessionId: session.id,
    amountCents: session.amount_total,
    currency: session.currency
  });
  db.markSessionPaid(session.id, session.payment_intent);
}

const router = express.Router();

router.post('/buy', requireAuth, async (req, res) => {
  const stripe = client();
  if (!stripe) return res.status(503).render('error', { title: 'Checkout unavailable', message: 'Payments are not configured yet. Please check back soon.' });
  if (db.hasPaid(req.user.id)) return res.redirect('/download');
  const s = config.settings;
  try {
    const session = await stripe.checkout.sessions.create({
      mode: 'payment',
      client_reference_id: String(req.user.id),
      customer_email: req.user.email,
      metadata: { userId: String(req.user.id) },
      line_items: [{
        quantity: 1,
        price_data: {
          currency: s.currency,
          unit_amount: s.priceCents,
          product_data: {
            name: `${s.productName} — lifetime license`,
            description: 'One-time purchase. Download all current and future builds.'
          }
        }
      }],
      success_url: `${config.publicUrl}/buy/success?session_id={CHECKOUT_SESSION_ID}`,
      cancel_url: `${config.publicUrl}/#pricing`
    });
    db.createPendingPurchase({ userId: req.user.id, sessionId: session.id, amountCents: s.priceCents, currency: s.currency });
    return res.redirect(303, session.url);
  } catch (err) {
    console.error('[stripe] checkout create failed:', err.message);
    return res.status(500).render('error', { title: 'Checkout error', message: 'Could not start checkout. Please try again.' });
  }
});

router.get('/buy/success', requireAuth, async (req, res) => {
  const stripe = client();
  const sid = String(req.query.session_id || '');
  if (stripe && sid) {
    try {
      const session = await stripe.checkout.sessions.retrieve(sid);
      if (session && session.payment_status === 'paid') fulfill(session);
    } catch (err) {
      console.error('[stripe] success reconcile failed:', err.message);
    }
  }
  return res.redirect('/download?purchase=success');
});

// Raw-body webhook handler (mounted separately in index.js).
async function webhookHandler(req, res) {
  const stripe = client();
  const secret = config.stripe.webhookSecret;
  if (!stripe) return res.status(503).end();
  let event;
  try {
    if (secret) {
      event = stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], secret);
    } else {
      event = JSON.parse(req.body.toString('utf8')); // unverified fallback if no secret set
    }
  } catch (err) {
    console.error('[stripe] webhook signature check failed:', err.message);
    return res.status(400).send(`Webhook Error: ${err.message}`);
  }
  if (event.type === 'checkout.session.completed' || event.type === 'checkout.session.async_payment_succeeded') {
    try { fulfill(event.data.object); } catch (err) { console.error('[stripe] fulfill failed:', err.message); }
  }
  res.json({ received: true });
}

module.exports = { router, webhookHandler };
