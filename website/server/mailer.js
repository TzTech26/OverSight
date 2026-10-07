'use strict';
/**
 * Email sender. If SMTP isn't configured, it logs the message (and any link)
 * to the server console so magic-link login still works during setup.
 */
const nodemailer = require('nodemailer');
const config = require('./config');

let transport = null;
function getTransport() {
  if (transport) return transport;
  if (!config.isSmtpConfigured()) return null;
  transport = nodemailer.createTransport({
    host: config.smtp.host,
    port: config.smtp.port,
    secure: config.smtp.port === 465,
    auth: { user: config.smtp.user, pass: config.smtp.pass }
  });
  return transport;
}

async function sendMail({ to, subject, text, html }) {
  const t = getTransport();
  if (!t) {
    console.log('\n[mailer] SMTP not configured — email NOT sent. Contents below:');
    console.log(`  To: ${to}\n  Subject: ${subject}\n  ${text}\n`);
    return { logged: true };
  }
  return t.sendMail({ from: config.smtp.from, to, subject, text, html });
}

module.exports = { sendMail };
