# Deploy BE len AWS EC2

BE chay bang Docker tren mot may EC2 (Ubuntu 24.04), Caddy nhan HTTPS o cong 443 roi chuyen vao `127.0.0.1:8080`.
Workflow `.github/workflows/azure-deploy.yml` (ten "Build and Deploy to AWS") tu deploy moi khi merge vao `main`:

1. `deployment-tests` va `processing-regression` phai pass.
2. Build image, push `ghcr.io/capstone-fa26se186-fire3d/fire3d-api:<commit>`.
3. `migration_preflight.py` kiem tra migration voi database that; loi thi dung, khong deploy.
4. SSH vao EC2, chay `sudo /usr/local/bin/fire3d-be-deploy <image> <commit 7 ky tu>`: may chu tu `docker pull` (bang tai khoan
   `docker login ghcr.io` cua chinh no) va khoi dong lai container `fire3d-api`.
5. Cho `GET /health/version` tra dung commit, roi smoke test `/health/ready`, `/api/learn/*`, `/openapi/v1.json`.

## Cai dat may chu (mot lan)

- Docker, Caddy, file env `/opt/fire3d-api/.env` (quyen 600) va lenh `/usr/local/bin/fire3d-be-deploy` do script `setup-be.sh`
  (ngoai repo, trong thu muc trien khai cua nhom) tao.
- `sudo docker login ghcr.io` bang token GitHub chi co quyen `read:packages`.
- Mot SSH key rieng cho CI (khong dung key ca nhan): them public key vao `~ubuntu/.ssh/authorized_keys`.
- Security group: cong 22 phai cho GitHub Actions vao (IP runner thay doi), nen mo 22 cho `0.0.0.0/0`; may chi cho dang nhap
  bang key (Ubuntu tren EC2 tat dang nhap bang mat khau). Cong 80/443 mo cho moi noi; khong mo 8080.

## Cau hinh GitHub (Settings -> Secrets and variables -> Actions)

| Loai | Ten | Gia tri |
|---|---|---|
| Secret | `AWS_BE_SSH_KEY` | Private key cua SSH key CI (toan bo noi dung file, ca dong BEGIN/END) |
| Variable | `AWS_BE_HOST` | Elastic IP cua may, vi du `52.221.57.57` |
| Variable | `AWS_BE_KNOWN_HOSTS` | Ket qua `ssh-keyscan -t ed25519 <ip>` (ghim host key, CI khong tin may la) |
| Variable | `AWS_BE_URL` | Dia chi HTTPS cong khai, vi du `https://52-221-57-57.sslip.io` |

Thieu secret/variable thi buoc "Deploy to AWS EC2" bao loi va dung.

## Quay lai ban truoc

May chu giu 3 image gan nhat (`sudo docker images fire3d-api`). Chay lai mot ban:

```bash
sudo fire3d-be-deploy fire3d-api:<tag> <tag>
```

Hoac chay lai workflow cua commit cu: GitHub -> Actions -> chon lan chay -> **Re-run all jobs**.

## Kiem tra va log

| Viec | Lenh tren may chu |
|---|---|
| Container dang chay | `sudo docker ps` |
| Log BE | `sudo docker logs --tail 100 fire3d-api` |
| Khoi dong lai | `sudo docker restart fire3d-api` |
| Log HTTPS | `sudo journalctl -u caddy -n 50 --no-pager` |
