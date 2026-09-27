(() => {
  const status = document.querySelector('#status');
  const button = document.querySelector('#verify');
  const fragment = new URLSearchParams(location.hash.slice(1));
  const legacy = new URLSearchParams(location.search);
  const token = fragment.get('token') || legacy.get('token');
  if (legacy.has('token')) history.replaceState(null, '', location.pathname + '#token=' + encodeURIComponent(token || ''));
  if (!token || !/^[a-f0-9]{64}$/i.test(token)) {
    status.textContent = 'Liên kết xác minh không hợp lệ. Hãy yêu cầu gửi lại email.';
    return;
  }
  status.textContent = 'Bấm nút bên dưới để xác minh email của bạn.';
  button.hidden = false;
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      const response = await fetch('/api/auth/verify-email', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' },
        body: JSON.stringify({ token })
      });
      if (response.status !== 204) throw new Error('verification failed');
      status.textContent = 'Email đã được xác minh. Bạn có thể đăng nhập.';
      history.replaceState(null, '', location.pathname);
      button.hidden = true;
    } catch {
      status.textContent = 'Liên kết đã hết hạn, đã dùng hoặc không hợp lệ. Hãy yêu cầu gửi lại email.';
      button.disabled = false;
    }
  });
})();
