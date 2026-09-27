document.querySelector('#form').addEventListener('submit', async event => {
  event.preventDefault();
  const status = document.querySelector('#status');
  const button = event.currentTarget.querySelector('button');
  button.disabled = true;
  try {
    const response = await fetch(new URL('../api/auth/resend-verification', location.href), {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' },
      body: JSON.stringify({ email: document.querySelector('#email').value })
    });
    status.textContent = response.status === 429
      ? 'Bạn đã yêu cầu quá nhiều lần. Vui lòng thử lại sau.'
      : response.ok
        ? 'Nếu tài khoản đang chờ xác minh, một liên kết xác minh sẽ được gửi đến email này.'
        : 'Không thể gửi yêu cầu lúc này. Vui lòng thử lại sau.';
  } catch {
    status.textContent = 'Không thể gửi yêu cầu lúc này. Vui lòng thử lại sau.';
  } finally {
    button.disabled = false;
  }
});
