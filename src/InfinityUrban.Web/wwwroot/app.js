const $ = (id) => document.getElementById(id);
let csrfToken;
let mode = 'login';
let busy = false;

function feedback(message) {
  $('feedback').textContent = message;
  $('feedback').hidden = !message;
}

function setMode(next, focus = true) {
  if (busy) return;
  mode = next;
  feedback('');
  $('tabs').hidden = next === 'recovery';
  $('panel-login').hidden = next !== 'login';
  $('panel-signup').hidden = next !== 'signup';
  $('recovery-form').hidden = next !== 'recovery';
  $('description').textContent = next === 'recovery' ? 'Informe seu e-mail para redefinir a senha' : 'Acesse sua conta ou solicite cadastro';
  for (const value of ['login', 'signup']) {
    $(`tab-${value}`).setAttribute('aria-selected', String(next === value));
    $(`tab-${value}`).tabIndex = next === value ? 0 : -1;
  }
  if (next !== 'login') $('login-password').value = '';
  if (next !== 'signup') $('signup-password').value = '';
  if (focus) $(next === 'signup' ? 'signup-name' : `${next}-email`).focus();
}

async function initializeForm() {
  const response = await fetch('/api/auth/form', { cache: 'no-store', credentials: 'same-origin' });
  if (!response.ok) throw new Error('Não foi possível preparar o login. Atualize a página.');
  csrfToken = (await response.json()).csrfToken;
}

async function api(path, body) {
  if (!csrfToken) await initializeForm();
  const response = await fetch(path, {
    method: 'POST', credentials: 'same-origin', cache: 'no-store',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
    body: JSON.stringify(body), signal: AbortSignal.timeout(20000),
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    if (response.status === 400) csrfToken = undefined;
    throw new Error(response.status === 429 ? 'Muitas tentativas. Aguarde um minuto para tentar novamente.' : data.error || 'Não foi possível concluir. Tente novamente.');
  }
  return data;
}

async function showSession() {
  const response = await fetch('/api/auth/session', { cache: 'no-store', credentials: 'same-origin', signal: AbortSignal.timeout(20000) });
  if (response.status === 401) {
    $('auth-card').hidden = false;
    $('session-card').hidden = true;
    return false;
  }
  if (!response.ok) throw new Error('Não foi possível verificar sua sessão. Tente novamente.');
  const user = await response.json();
  if (user.member?.isOperator || user.member?.isRestrictedOwner) { location.replace('/acomodacoes'); return true; }
  $('session-email').textContent = user.email;
  $('auth-card').hidden = true;
  $('session-card').hidden = false;
  $('login-password').value = '';
  csrfToken = undefined;
  return true;
}

for (const value of ['login', 'signup']) {
  $(`tab-${value}`).addEventListener('click', () => setMode(value, false));
  $(`tab-${value}`).addEventListener('keydown', (event) => {
    if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
      event.preventDefault();
      const next = event.key === 'Home' ? 'login' : event.key === 'End' ? 'signup' : value === 'login' ? 'signup' : 'login';
      setMode(next, false);
      $(`tab-${next}`).focus();
    }
  });
}
$('forgot-password').addEventListener('click', () => setMode('recovery'));
$('back-to-login').addEventListener('click', () => setMode('login'));
for (const button of document.querySelectorAll('[data-password]')) {
  button.addEventListener('click', () => {
    const input = $(button.dataset.password);
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    button.setAttribute('aria-label', show ? 'Ocultar senha' : 'Mostrar senha');
    button.setAttribute('aria-pressed', String(show));
  });
}

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  if (busy) return;
  busy = true;
  feedback('');
  const button = event.currentTarget.querySelector('[type=submit]');
  button.disabled = true;
  button.textContent = 'Entrando…';
  try {
    await api('/api/auth/login', { email: $('login-email').value.trim(), password: $('login-password').value });
    if (!await showSession()) throw new Error('Sua sessão expirou. Entre novamente.');
  } catch (error) {
    feedback(error instanceof TypeError || error.name === 'TimeoutError' ? 'Verifique sua conexão e tente novamente.' : error.message);
    $('login-password').value = '';
  } finally {
    busy = false;
    button.disabled = false;
    button.textContent = 'Entrar';
  }
});

// These views reproduce the reference; writes remain closed until the existing
// approval schema and recovery redirects have been verified for the new portal.
for (const id of ['signup-form', 'recovery-form']) {
  $(id).addEventListener('submit', (event) => {
    event.preventDefault();
    $('signup-password').value = '';
    feedback(id === 'signup-form'
      ? 'O cadastro neste novo portal ainda está em preparação. Entre com uma conta existente.'
      : 'A recuperação de senha neste novo portal ainda está em preparação. Utilize o site atual ou contate a administração.');
  });
}

$('logout').addEventListener('click', async () => {
  $('logout').disabled = true;
  try {
    const result = await api('/api/auth/logout', {});
    $('session-card').hidden = true;
    $('auth-card').hidden = false;
    csrfToken = undefined;
    setMode('login');
    if (!result.revoked) feedback('Você saiu deste portal. Não foi possível confirmar o encerramento da sessão no serviço de autenticação.');
  } catch {
    $('session-feedback').textContent = 'Não foi possível sair. Verifique sua conexão e tente novamente.';
    $('session-feedback').hidden = false;
  } finally { $('logout').disabled = false; }
});

initializeForm().then(showSession).catch((error) => feedback(error.message));
window.addEventListener('pageshow', (event) => { if (event.persisted) showSession().catch(() => location.reload()); });
