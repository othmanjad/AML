# التشغيل على Docker (خادم Windows)

يشغّل `docker-compose.yml` ثلاث حاويات:

| الحاوية | المحتوى | المنفذ |
|---|---|---|
| `sql` | SQL Server 2022 (بيانات دائمة في volume `sql-data`) | داخلي فقط |
| `api` | واجهة .NET 8 + المهام المجدولة، تُنشئ قاعدة البيانات وتطبّق الترحيلات تلقائياً | داخلي فقط (8080) |
| `web` | واجهة المستخدم (nginx) وتمرّر `/api` إلى الـ API | `WEB_PORT` (افتراضي 8080) |

## المتطلبات على الخادم
1. **Docker بوضع Linux containers** (صورة SQL Server وصور النظام صور Linux):
   - Windows Server 2022 / Windows 11: ثبّت WSL2 ثم Docker Desktop (أو Docker Engine داخل توزيعة WSL).
   - تحقق: `docker info --format "{{.OSType}}"` يجب أن تكون النتيجة `linux`. إن كانت `windows` فبدّل إلى Linux containers (Docker Desktop → Switch to Linux containers).
2. Git لتنزيل المشروع (أو انسخ مجلد المشروع يدوياً).
3. ذاكرة 8 GB على الأقل (SQL Server يحتاج 2 GB) ومساحة قرص 20 GB.
4. افتح المنفذ `WEB_PORT` في جدار حماية Windows.

## خطوات التشغيل (PowerShell)
```powershell
git clone https://github.com/othmanjad/aml.git C:\aml
cd C:\aml
git checkout claude/aml-platform-implementation

# إنشاء ملف الأسرار .env بقيم عشوائية (لا يُرفع إلى Git)
powershell -ExecutionPolicy Bypass -File deploy\docker\generate-env.ps1 -PublicUrl http://SERVER-IP:8080

docker compose up -d --build        # أول مرة: 5–15 دقيقة لتنزيل الصور وبناء المشروع
docker compose ps                   # sql يجب أن تكون healthy
docker compose logs -f api          # متابعة الإقلاع (ترحيل قاعدة البيانات وبيانات العرض)
```
افتح `http://SERVER-IP:8080`، وسجّل الدخول بأحد المستخدمين: `admin`, `manager`, `officer`, `senior`, `mlro`, `investigator`, `auditor`, `ops` — كلمة المرور هي قيمة `DEVAUTH_SEED_PASSWORD` في ملف `.env`.
توثيق الـ API التفاعلي: `http://SERVER-IP:8080/swagger` (عند `SWAGGER_ENABLED=true`).

## الإعدادات (`.env`)
راجع `deploy/docker/.env.example`. أهمها:
- `SQL_SA_PASSWORD`، ومفاتيح التشفير `SECURITY_*` و`AUTH_SIGNING_KEY`: **احتفظ بنسخة آمنة**؛ فقدان `SECURITY_ENCRYPTION_KEY` يجعل الحقول المشفّرة غير قابلة للقراءة.
- `SEED_DEMO_DATA=true` يضيف بيانات عرض وهمية؛ اجعلها `false` لبيئة نظيفة.
- `INTEGRATION_CLIENT_SECRET`: كلمة سر عملاء التكامل `jo-demo-core` و`jo-demo-wallet` و`jo-demo-cards` لإرسال الحركات من الأنظمة المصدر (`POST /api/auth/token`).
- `ASPNETCORE_ENVIRONMENT=Staging` للاختبار. بيئة الإنتاج تحتاج مزوّد هوية مؤسسي (`AUTH_AUTHORITY`) وإيقاف `DEVAUTH_ENABLED` وشهادة HTTPS.

## أوامر الصيانة
```powershell
git pull; docker compose up -d --build              # تحديث النظام إلى آخر إصدار
docker compose restart api
docker compose down                                    # إيقاف (البيانات تبقى)
docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<SQL_SA_PASSWORD>" -C -Q "BACKUP DATABASE AmlDb TO DISK='/var/opt/mssql/backup/AmlDb.bak'"
docker compose down -v                                 # حذف كل شيء بما فيه قاعدة البيانات (لا رجعة فيه)
```

## HTTPS
للتجربة الداخلية يعمل النظام على HTTP. قبل استخدام بيانات حقيقية: ضع شهادة TLS على nginx أو موازن الأحمال، واجعل `HTTPS_REDIRECT=true`، واستخدم مزوّد هوية مؤسسي.

## ملاحظات
- لا يُرسل النظام أي بلاغ أو أمر تجميد إلى جهات حقيقية: محوّل النظام البنكي في هذا الإعداد محاكٍ (`Integrations__CoreAdapter=Mock`) وإرسال البلاغات يدوي.
- القيم التنظيمية (الحدود والمدد) أمثلة تنتظر تأكيد فريق الامتثال.
- `MSSQL_PID=Developer` مجاني للاختبار فقط؛ للإنتاج استخدم ترخيص Standard/Enterprise أو SQL Server خارجي (غيّر `ConnectionStrings__Aml`).

## التشغيل على Ubuntu Server (22.04 / 24.04 / 26.04 LTS)
على Linux تعمل الحاويات مباشرة دون WSL، وهو الخيار الأبسط. يلزم معالج x86-64 (Intel/AMD) لأن صورة SQL Server لا تدعم ARM.
```bash
# 1) تثبيت Docker Engine من مستودع Docker الرسمي
sudo apt-get update && sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" | sudo tee /etc/apt/sources.list.d/docker.list
sudo apt-get update && sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker $USER && newgrp docker

# 2) المشروع والأسرار والتشغيل
git clone https://github.com/othmanjad/aml.git ~/aml && cd ~/aml && git checkout claude/aml-platform-implementation
./deploy/docker/generate-env.sh http://SERVER-IP:8080
docker compose up -d --build
sudo ufw allow 8080/tcp   # إن كان جدار الحماية مفعّلاً
```
