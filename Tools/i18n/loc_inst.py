# -*- coding: utf-8 -*-
"""Round four: the long descriptions on the installer, controller-health and maintenance screens.

Brand and product names stay as they are (MSI Center M, HidHide, usbip, VIIPER, Game Bar, VID_0DB0),
and so do the controller glyphs the sentences point at."""
INSTALLER = {

# ---- maintenance ---------------------------------------------------------
"Update ClawTweaks from \"Update & Release\" first.":
    (u"ClawTweaks zuerst unter \"Update & Release\" aktualisieren.",
     u"Mettez d'abord ClawTweaks à jour via \"Update & Release\".",
     u"먼저 \"Update & Release\"에서 ClawTweaks를 업데이트하세요.",
     u"Actualiza antes ClawTweaks desde \"Update & Release\"."),
"• Global and per-game profiles (TDP, fan curves, controller, gyro)":
    (u"• Globale und Spielprofile (TDP, Lüfterkurven, Controller, Gyro)",
     u"• Profils globaux et par jeu (TDP, courbes, manette, gyro)",
     u"• 전역 및 게임별 프로필 (TDP, 팬 커브, 컨트롤러, 자이로)",
     u"• Perfiles globales y por juego (TDP, ventiladores, mando, giro)"),
"• Helper settings (global TDP, fan curve, controller emulation)":
    (u"• Helper-Einstellungen (globales TDP, Lüfterkurve, Controller-Emulation)",
     u"• Réglages du helper (TDP global, courbe, émulation manette)",
     u"• 헬퍼 설정 (전역 TDP, 팬 커브, 컨트롤러 에뮬레이션)",
     u"• Ajustes del helper (TDP global, ventilador, emulación de mando)"),
"An automatic backup of your current settings is created before the reset, so you can restore it later from Restore Backup. The Game Bar will be closed — reopen it (Win+G) afterwards.":
    (u"Vor dem Zurücksetzen wird automatisch ein Backup deiner Einstellungen angelegt, das du später über Backup zurückholen kannst. Die Game Bar wird geschlossen — danach mit Win+G wieder öffnen.",
     u"Une sauvegarde automatique de vos réglages est créée avant la remise à zéro ; vous pourrez la restaurer plus tard. La Game Bar sera fermée — rouvrez-la avec Win+G.",
     u"초기화 전에 현재 설정이 자동으로 백업되며 나중에 백업 복원에서 되돌릴 수 있습니다. Game Bar가 닫히므로 나중에 Win+G로 다시 여세요.",
     u"Antes del restablecimiento se crea una copia automática de tus ajustes, que podrás restaurar después. La Game Bar se cerrará — vuelve a abrirla con Win+G."),
"Saves all your profiles and settings into a single ZIP. The Game Bar is briefly closed so the widget's data can be copied — reopen it (Win+G) afterwards.":
    (u"Sichert alle Profile und Einstellungen in eine ZIP-Datei. Die Game Bar wird kurz geschlossen, damit die Widget-Daten kopiert werden können — danach mit Win+G wieder öffnen.",
     u"Enregistre tous vos profils et réglages dans un seul ZIP. La Game Bar se ferme brièvement pour copier les données du widget — rouvrez-la avec Win+G.",
     u"모든 프로필과 설정을 하나의 ZIP으로 저장합니다. 위젯 데이터를 복사하기 위해 Game Bar가 잠시 닫히므로 나중에 Win+G로 다시 여세요.",
     u"Guarda todos tus perfiles y ajustes en un único ZIP. La Game Bar se cierra un momento para copiar los datos del widget — reábrela con Win+G."),
"Pick a backup to restore. A safety copy of the current state is taken automatically first, then the helper restarts to load the restored settings.":
    (u"Wähle ein Backup zum Zurückholen. Vom aktuellen Stand wird zuerst automatisch eine Sicherung angelegt, danach startet der Helper neu und lädt die Einstellungen.",
     u"Choisissez un backup à restaurer. Une copie de sécurité de l'état actuel est faite d'abord, puis le helper redémarre pour charger les réglages.",
     u"복원할 백업을 선택하세요. 현재 상태를 먼저 자동으로 백업한 뒤 헬퍼가 다시 시작하며 설정을 불러옵니다.",
     u"Elige una copia para restaurar. Primero se guarda automáticamente el estado actual y después el helper se reinicia para cargar los ajustes."),
"A safety copy of your current settings is saved first, the Game Bar closes, the backup is written back, and the helper restarts. Reopen the Game Bar (Win+G) when it's done.":
    (u"Zuerst wird der aktuelle Stand gesichert, dann schließt die Game Bar, das Backup wird zurückgeschrieben und der Helper startet neu. Danach die Game Bar mit Win+G wieder öffnen.",
     u"Une copie de vos réglages actuels est enregistrée, la Game Bar se ferme, le backup est réécrit et le helper redémarre. Rouvrez la Game Bar (Win+G) ensuite.",
     u"현재 설정을 먼저 백업하고 Game Bar를 닫은 뒤 백업을 되돌리고 헬퍼가 다시 시작합니다. 끝나면 Win+G로 Game Bar를 여세요.",
     u"Primero se guarda una copia de tus ajustes, se cierra la Game Bar, se escribe la copia y el helper se reinicia. Reabre la Game Bar (Win+G) al terminar."),

# ---- controller health ---------------------------------------------------
"The native controller must be clean before the virtual controller mode can work. In virtual mode the physical pad is hidden and one virtual VIIPER controller is active; in hardware mode the physical pad is used directly.":
    (u"Der native Controller muss sauber sein, bevor der virtuelle Controller-Modus funktioniert. Im virtuellen Modus wird der physische Pad versteckt und ein virtueller VIIPER-Controller ist aktiv; im Hardware-Modus wird der physische Pad direkt benutzt.",
     u"La manette native doit être propre avant que le mode virtuel puisse fonctionner. En mode virtuel la manette physique est masquée et une manette VIIPER virtuelle est active ; en mode matériel la manette physique est utilisée directement.",
     u"가상 컨트롤러 모드가 동작하려면 기본 컨트롤러가 깨끗해야 합니다. 가상 모드에서는 물리 패드가 숨겨지고 VIIPER 가상 컨트롤러가 활성화되며, 하드웨어 모드에서는 물리 패드를 직접 사용합니다.",
     u"El mando nativo debe estar limpio antes de que funcione el modo de mando virtual. En modo virtual el mando físico se oculta y hay un mando virtual VIIPER activo; en modo hardware se usa el mando físico directamente."),
"In virtual mode the physical pad is hidden and one virtual VIIPER controller is active; in hardware mode the physical pad is used directly.":
    (u"Im virtuellen Modus wird der physische Pad versteckt und ein virtueller VIIPER-Controller ist aktiv; im Hardware-Modus wird der physische Pad direkt benutzt.",
     u"En mode virtuel la manette physique est masquée et une manette VIIPER virtuelle est active ; en mode matériel la manette physique est utilisée directement.",
     u"가상 모드에서는 물리 패드가 숨겨지고 VIIPER 가상 컨트롤러가 활성화되며, 하드웨어 모드에서는 물리 패드를 직접 사용합니다.",
     u"En modo virtual el mando físico se oculta y hay un mando virtual VIIPER activo; en modo hardware se usa el mando físico directamente."),
"NOT detected (VID_0DB0). The controller is missing, or MSI Center M has taken it over.":
    (u"NICHT erkannt (VID_0DB0). Der Controller fehlt, oder MSI Center M hat ihn übernommen.",
     u"NON détecté (VID_0DB0). La manette est absente, ou MSI Center M l'a prise.",
     u"감지되지 않음 (VID_0DB0). 컨트롤러가 없거나 MSI Center M이 가져갔습니다.",
     u"NO detectado (VID_0DB0). Falta el mando, o MSI Center M lo ha tomado."),
"Not mounted right now. Expected only while virtual mode is running — normal at setup time.":
    (u"Gerade nicht eingebunden. Nur im virtuellen Modus zu erwarten — bei der Einrichtung normal.",
     u"Pas monté actuellement. Attendu seulement en mode virtuel — normal à l'installation.",
     u"현재 연결되어 있지 않습니다. 가상 모드에서만 나타나며 설치 중에는 정상입니다.",
     u"Ahora no está montado. Solo se espera en modo virtual — normal al instalar."),
"Running — it can fight ClawTweaks for the controller and LED. You'll be guided to deactivate it after the app is installed.":
    (u"Läuft — es kann ClawTweaks um Controller und LED streitig machen. Nach der Installation wirst du durch das Abschalten geführt.",
     u"En cours — il peut disputer la manette et la LED à ClawTweaks. Vous serez guidé pour le désactiver après l'installation.",
     u"실행 중 — 컨트롤러와 LED를 두고 ClawTweaks와 충돌할 수 있습니다. 설치 후 비활성화 방법을 안내합니다.",
     u"En ejecución — puede disputarle a ClawTweaks el mando y el LED. Se te guiará para desactivarlo tras instalar."),
"Present — a common double-input source. Check Steam Input if you see doubled inputs.":
    (u"Vorhanden — eine häufige Quelle für doppelte Eingaben. Bei Doppeleingaben Steam Input prüfen.",
     u"Présent — source fréquente de double saisie. Vérifiez Steam Input si les entrées doublent.",
     u"존재함 — 입력이 중복되는 흔한 원인입니다. 중복 입력이 보이면 Steam Input을 확인하세요.",
     u"Presente — causa habitual de entradas dobles. Revisa Steam Input si se duplican."),
"Final clean-up and check. MSI Center M is handled here (after installation) because it fights ClawTweaks for the controller and LED.":
    (u"Letzte Aufräumarbeiten und Prüfung. MSI Center M kommt hier dran (nach der Installation), weil es ClawTweaks um Controller und LED streitig macht.",
     u"Dernier nettoyage et vérification. MSI Center M est traité ici (après l'installation) car il dispute la manette et la LED à ClawTweaks.",
     u"마지막 정리 및 확인입니다. MSI Center M은 컨트롤러와 LED를 두고 ClawTweaks와 충돌하므로 설치 후 여기서 처리합니다.",
     u"Limpieza y comprobación finales. MSI Center M se trata aquí (tras instalar) porque le disputa a ClawTweaks el mando y el LED."),
"Installed but not running (fine). Uninstall it from Windows Settings › Apps if you want it gone permanently.":
    (u"Installiert, läuft aber nicht (in Ordnung). Über Windows-Einstellungen › Apps deinstallieren, wenn es dauerhaft weg soll.",
     u"Installé mais pas lancé (correct). Désinstallez-le depuis Paramètres Windows › Applications pour le supprimer définitivement.",
     u"설치되어 있으나 실행 중 아님 (정상). 완전히 없애려면 Windows 설정 › 앱에서 제거하세요.",
     u"Instalado pero sin ejecutarse (bien). Desinstálalo en Configuración de Windows › Aplicaciones para quitarlo del todo."),

# ---- install phase -------------------------------------------------------
"Trusts the signing certificate, installs the app package, opens the Game Bar and waits for the helper. Safe to run again on an update.":
    (u"Vertraut dem Signaturzertifikat, installiert das App-Paket, öffnet die Game Bar und wartet auf den Helper. Bei einem Update gefahrlos wiederholbar.",
     u"Approuve le certificat, installe le paquet, ouvre la Game Bar et attend le helper. Peut être relancé sans risque lors d'une mise à jour.",
     u"서명 인증서를 신뢰하고 앱 패키지를 설치한 뒤 Game Bar를 열고 헬퍼를 기다립니다. 업데이트 시 다시 실행해도 안전합니다.",
     u"Confía en el certificado, instala el paquete, abre la Game Bar y espera al helper. Se puede repetir sin riesgo en una actualización."),
"No .cer bundled with this setup build (dev run).":
    (u"Kein .cer in diesem Setup-Build enthalten (Dev-Lauf).",
     u"Aucun .cer fourni avec ce build (exécution dev).",
     u"이 설치 빌드에 .cer이 없습니다 (개발 실행).",
     u"Este build no incluye .cer (ejecución de desarrollo)."),
"No .msix bundled with this setup build (dev run).":
    (u"Kein .msix in diesem Setup-Build enthalten (Dev-Lauf).",
     u"Aucun .msix fourni avec ce build (exécution dev).",
     u"이 설치 빌드에 .msix가 없습니다 (개발 실행).",
     u"Este build no incluye .msix (ejecución de desarrollo)."),
"This is a scaffold build without a bundled package, so there's nothing to install here. In a real setup bundle the .msix and .cer sit next to this exe.":
    (u"Das ist ein Gerüst-Build ohne mitgeliefertes Paket, hier gibt es also nichts zu installieren. In einem echten Setup-Bündel liegen .msix und .cer neben dieser exe.",
     u"Ceci est un build d'ossature sans paquet, il n'y a donc rien à installer ici. Dans un vrai bundle, le .msix et le .cer sont à côté de cet exe.",
     u"이 빌드는 패키지가 포함되지 않은 골격 빌드라 설치할 것이 없습니다. 실제 설치 번들에서는 .msix와 .cer이 이 exe 옆에 있습니다.",
     u"Este es un build de andamiaje sin paquete, así que no hay nada que instalar. En un bundle real el .msix y el .cer están junto a este exe."),

# ---- tools phase ---------------------------------------------------------
"HidHide and usbip install kernel drivers. Reboot the device once after installing them, then run this setup again.":
    (u"HidHide und usbip installieren Kerneltreiber. Nach der Installation das Gerät einmal neu starten und dieses Setup erneut ausführen.",
     u"HidHide et usbip installent des pilotes noyau. Redémarrez l'appareil une fois après les avoir installés, puis relancez ce setup.",
     u"HidHide와 usbip은 커널 드라이버를 설치합니다. 설치 후 장치를 한 번 재부팅한 다음 이 설치를 다시 실행하세요.",
     u"HidHide y usbip instalan controladores de núcleo. Reinicia el dispositivo una vez tras instalarlos y vuelve a ejecutar este setup."),
"usbip  (required for virtual controller)":
    (u"usbip  (für den virtuellen Controller nötig)",
     u"usbip  (requis pour la manette virtuelle)",
     u"usbip  (가상 컨트롤러에 필요)",
     u"usbip  (necesario para el mando virtual)"),

# ---- detect phase --------------------------------------------------------
"Next: controller health, required tools, the signing certificate, then the app itself. Each step re-checks live and won't let you continue until it's satisfied.":
    (u"Als Nächstes: Controller-Zustand, benötigte Tools, das Signaturzertifikat, dann die App selbst. Jeder Schritt prüft live und lässt dich erst weiter, wenn er erfüllt ist.",
     u"Ensuite : état de la manette, outils requis, certificat de signature, puis l'app elle-même. Chaque étape revérifie en direct et ne laisse pas continuer avant d'être satisfaite.",
     u"다음 순서: 컨트롤러 상태, 필요한 도구, 서명 인증서, 그리고 앱 자체입니다. 각 단계는 실시간으로 다시 확인하며 충족될 때까지 진행할 수 없습니다.",
     u"A continuación: estado del mando, herramientas necesarias, el certificado de firma y luego la app. Cada paso se revisa en vivo y no deja continuar hasta cumplirse."),

# ---- home ----------------------------------------------------------------
"Almost — but in the wrong place":
    (u"Fast — aber am falschen Ort", u"Presque — mais au mauvais endroit",
     u"거의 — 하지만 위치가 잘못됨", u"Casi — pero en el sitio erróneo"),
}
