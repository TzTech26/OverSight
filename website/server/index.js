'use strict';
/**
 * Oversight store — Express entry point.
 * Marketing site + Google/email login + Stripe one-time purchase +
 * entitlement-gated downloads synced from the NAS + owner admin console.
 */
const path = require('path');
const express = require('express');
const helmet = require('helmet');
const session = require('express-session');
const SQLiteStore = require('connect-sqlite3')(session);

const config = require('./config');
const db = require('./db');
const sync = require('./sync');
const { loadUser } = require('./middleware');
const authRoutes = require('./auth');
const { router: stripeRoutes, webhookHandler } = require('./stripe');
const downloadRoutes = require('./downloads');
const adminRoutes = require('./admin');

const app = express();
app.set('trust proxy', 1); // behind Cloudflare Tunnel / reverse proxy
app.set('view engine', 'ejs');
app.set('views', path.join(__dirname, '..', 'views'));

app.use(helmet({
  contentSecurityPolicy: {
    directives: {
      defaultSrc: ["'self'"],
      scriptSrc: ["'self'", "'unsafe-inline'"],
      styleSrc: ["'self'", "'unsafe-inline'"],
      imgSrc: ["'self'", 'data:'],
      formAction: ["'self'", 'https://checkout.stripe.com'],
      frameAncestors: ["'none'"]
    }
  }
}));

// Stripe webhook needs the RAW body — mount BEFORE the JSON/urlencoded parsers.
app.post('/stripe/webhook', express.raw({ type: 'application/json' }), webhookHandler);

app.use(express.urlencoded({ extended: false }));
app.use(express.json());
app.use('/static', express.static(path.join(__dirname, '..', 'public'), { maxAge: '1h' }));

app.use(session({
  store: new SQLiteStore({ db: 'sessions.db', dir: config.DATA_DIR }),
  secret: config.sessionSecret,
  resave: false,
  saveUninitialized: false,
  cookie: { httpOnly: true, sameSite: 'lax', secure: 'auto', maxAge: 30 * 24 * 3600 * 1000 }
}));

app.use(loadUser);

// Expose common values to every template.
app.use((req, res, next) => {
  res.locals.settings = config.settings;
  res.locals.priceDisplay = formatPrice(config.settings.priceCents, config.settings.currency);
  res.locals.year = new Date().getFullYear();
  next();
});

// --- Marketing landing page -------------------------------------------------
app.get('/', (req, res) => {
  res.render('index', {
    title: `${config.settings.productName} — ${config.settings.tagline}`,
    def: sync.defaultRelease()
  });
});

app.get('/healthz', (req, res) => res.json({ ok: true, sync: sync.getStatus() }));

// --- Feature routers --------------------------------------------------------
app.use(authRoutes);
app.use(stripeRoutes);
app.use(downloadRoutes);
app.use(adminRoutes);

// --- 404 + error handling ---------------------------------------------------
app.use((req, res) => res.status(404).render('error', { title: 'Not found', message: 'That page does not exist.' }));
app.use((err, req, res, next) => {
  console.error('[error]', err);
  res.status(500).render('error', { title: 'Server error', message: 'Something went wrong.' });
});

function formatPrice(cents, currency) {
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: (currency || 'usd').toUpperCase() })
      .format((cents || 0) / 100);
  } catch { return `$${((cents || 0) / 100).toFixed(2)}`; }
}

sync.startScheduler();

app.listen(config.port, config.host, () => {
  console.log(`\nOversight store running at http://${config.host}:${config.port}`);
  console.log(`Public URL: ${config.publicUrl}`);
  console.log(`Admin emails: ${config.adminEmails.join(', ') || '(none set — set ADMIN_EMAILS in .env)'}`);
  if (!config.isStripeConfigured()) console.log('NOTE: Stripe not configured yet — set keys in .env or /admin.');
  if (!config.isGoogleConfigured()) console.log('NOTE: Google sign-in not configured — email magic-link still works.');
});
