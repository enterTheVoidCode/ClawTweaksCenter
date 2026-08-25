# -*- coding: utf-8 -*-
"""Round six: the gaps reported from the device, plus the rest of the installer prose.

"Library Settings" is here on purpose even though Home headings are otherwise English - it was
asked for by name, because in German the word IS the translated one (Bibliothek)."""
GAPS = {

# ---- Home ----------------------------------------------------------------
# The tile and the SCREEN it opens must not both read "Bibliothek" - one is the library, the
# other is its settings, and on Home they sit next to each other.
"Library Settings":  (u"Bibliothek Einstellungen", u"Réglages bibliothèque", u"라이브러리 설정", u"Ajustes de biblioteca"),
"Windowed":          (u"Fenstermodus", u"Mode fenêtré", u"창 모드", u"En ventana"),

# ---- maintenance cards: the DESCRIPTIONS under the titles ----------------
"Wipe every ClawTweaks setting back to a clean state (all profiles, fan curves, TDP, controller). This cannot be undone — take a backup first if unsure.":
    (u"Setzt jede ClawTweaks-Einstellung zurück (alle Profile, Lüfterkurven, TDP, Controller). Das lässt sich nicht rückgängig machen — im Zweifel vorher ein Backup anlegen.",
     u"Remet chaque réglage ClawTweaks à zéro (profils, courbes, TDP, manette). Irréversible — faites un backup avant en cas de doute.",
     u"모든 ClawTweaks 설정을 초기 상태로 되돌립니다 (프로필, 팬 커브, TDP, 컨트롤러). 되돌릴 수 없으니 확실하지 않으면 먼저 백업하세요.",
     u"Restablece todos los ajustes de ClawTweaks (perfiles, ventiladores, TDP, mando). No se puede deshacer — haz una copia antes si dudas."),
"Save all your profiles and settings into a single ZIP you can restore later.":
    (u"Sichert alle Profile und Einstellungen in eine ZIP-Datei, die du später zurückspielen kannst.",
     u"Enregistre tous vos profils et réglages dans un ZIP que vous pourrez restaurer.",
     u"모든 프로필과 설정을 나중에 복원할 수 있는 ZIP 하나로 저장합니다.",
     u"Guarda todos tus perfiles y ajustes en un ZIP que podrás restaurar."),
"Bring back a previous backup. A safety copy of the current state is taken automatically first.":
    (u"Spielt ein früheres Backup zurück. Vom aktuellen Stand wird vorher automatisch eine Sicherung angelegt.",
     u"Restaure un backup précédent. Une copie de sécurité de l'état actuel est faite d'abord.",
     u"이전 백업을 되돌립니다. 현재 상태는 먼저 자동으로 백업됩니다.",
     u"Restaura una copia anterior. Antes se guarda automáticamente el estado actual."),
"Will be saved to":  (u"Wird gespeichert unter", u"Sera enregistré dans",
                      u"저장 위치", u"Se guardará en"),

# ---- cover art picker ----------------------------------------------------
"Searching…":        (u"Suche läuft…", u"Recherche…", u"검색 중…", u"Buscando…"),

# ---- shell / first run ---------------------------------------------------
"Connecting to the helper…": (u"Verbinde mit dem Helper…", u"Connexion au helper…",
                              u"헬퍼에 연결 중…", u"Conectando con el helper…"),
"Center home":       (u"Center-Startseite", u"Accueil Center", u"Center 홈", u"Inicio de Center"),
"Done here?":        (u"Hier fertig?", u"Terminé ici ?", u"여기서 끝났나요?", u"¿Listo aquí?"),
"Not now":           (u"Jetzt nicht", u"Plus tard", u"나중에", u"Ahora no"),
"What's new":        (u"Neuerungen", u"Nouveautés", u"새로운 점", u"Novedades"),
"An older ClawTweaks Center is still installed":
    (u"Ein älteres ClawTweaks Center ist noch installiert",
     u"Un ancien ClawTweaks Center est encore installé",
     u"이전 ClawTweaks Center가 아직 설치되어 있습니다",
     u"Sigue instalado un ClawTweaks Center anterior"),
"Remove the old version": (u"Alte Version entfernen", u"Supprimer l'ancienne version",
                           u"이전 버전 제거", u"Quitar la versión antigua"),
"Removing it needs administrator rights. ClawTweaks Center never asks for those — the button below hands the job to Windows' own uninstaller.":
    (u"Das Entfernen braucht Adminrechte. ClawTweaks Center fragt nie danach — der Knopf unten übergibt die Aufgabe an die Windows-Deinstallation.",
     u"Le retrait demande des droits administrateur. ClawTweaks Center n'en demande jamais — le bouton ci-dessous confie la tâche au désinstalleur de Windows.",
     u"제거하려면 관리자 권한이 필요합니다. ClawTweaks Center는 절대 요구하지 않으며, 아래 버튼이 Windows 제거 프로그램에 넘깁니다.",
     u"Quitarlo requiere permisos de administrador. ClawTweaks Center nunca los pide — el botón de abajo deja la tarea al desinstalador de Windows."),
"The old version's uninstaller is running — confirm its prompt. This notice disappears once it's gone.":
    (u"Die Deinstallation der alten Version läuft — bestätige ihre Abfrage. Der Hinweis verschwindet, sobald sie weg ist.",
     u"Le désinstalleur de l'ancienne version tourne — confirmez sa demande. Cet avis disparaît une fois terminé.",
     u"이전 버전의 제거 프로그램이 실행 중입니다 — 표시되는 확인을 눌러 주세요. 제거되면 이 안내는 사라집니다.",
     u"El desinstalador de la versión antigua está en marcha — confirma su aviso. Este mensaje desaparece al terminar."),
"Transition to new Center App - Uninstall the old version":
    (u"Umstieg auf das neue Center — alte Version deinstallieren",
     u"Passage au nouveau Center — désinstaller l'ancienne version",
     u"새 Center로 전환 — 이전 버전 제거",
     u"Cambio al nuevo Center — desinstala la versión antigua"),

# ---- install run ---------------------------------------------------------
"Open download pages": (u"Downloadseiten öffnen", u"Ouvrir les pages", u"다운로드 페이지 열기", u"Abrir páginas"),
"Close MSI Center M":  (u"MSI Center M schließen", u"Fermer MSI Center M",
                        u"MSI Center M 닫기", u"Cerrar MSI Center M"),
"Show the certificate in Explorer": (u"Zertifikat im Explorer zeigen", u"Voir le certificat dans l'Explorateur",
                                     u"탐색기에서 인증서 보기", u"Ver el certificado en el Explorador"),
"Certificate already trusted.": (u"Zertifikat ist bereits vertraut.", u"Certificat déjà approuvé.",
                                 u"인증서가 이미 신뢰됨.", u"El certificado ya es de confianza."),
"Required tools (HidHide, RTSS, usbip, PawnIO) already installed.":
    (u"Benötigte Tools (HidHide, RTSS, usbip, PawnIO) sind installiert.",
     u"Outils requis (HidHide, RTSS, usbip, PawnIO) déjà installés.",
     u"필요한 도구(HidHide, RTSS, usbip, PawnIO)가 이미 설치됨.",
     u"Herramientas necesarias (HidHide, RTSS, usbip, PawnIO) ya instaladas."),
"Download and package install in progress.": (u"Download und Paketinstallation laufen.",
                                              u"Téléchargement et installation en cours.",
                                              u"다운로드 및 패키지 설치 진행 중.",
                                              u"Descarga e instalación en curso."),
"No installable package found after staging.": (u"Nach dem Bereitstellen kein installierbares Paket gefunden.",
                                                u"Aucun paquet installable après la préparation.",
                                                u"준비 후 설치 가능한 패키지를 찾지 못했습니다.",
                                                u"No se encontró paquete instalable tras preparar."),
"Installation complete": (u"Installation abgeschlossen", u"Installation terminée",
                          u"설치 완료", u"Instalación completada"),
"No restart necessary.": (u"Kein Neustart nötig.", u"Aucun redémarrage requis.",
                          u"재부팅이 필요 없습니다.", u"No hace falta reiniciar."),
"Open the Game Bar manually (Win+G).": (u"Game Bar von Hand öffnen (Win+G).",
                                        u"Ouvrez la Game Bar à la main (Win+G).",
                                        u"Game Bar를 직접 여세요 (Win+G).",
                                        u"Abre la Game Bar a mano (Win+G)."),
"Waiting for UAC…":   (u"Warte auf UAC…", u"Attente de l'UAC…", u"UAC 대기 중…", u"Esperando a UAC…"),
"A confirmation prompt appeared — please confirm it to continue.":
    (u"Eine Bestätigung ist aufgetaucht — bitte bestätigen, um fortzufahren.",
     u"Une demande de confirmation est apparue — confirmez pour continuer.",
     u"확인 창이 나타났습니다 — 계속하려면 확인하세요.",
     u"Apareció una confirmación — acéptala para continuar."),
"Timed out":          (u"Zeit abgelaufen", u"Délai dépassé", u"시간 초과", u"Tiempo agotado"),
"Checking for duplicate helpers…": (u"Suche nach doppelten Helpern…", u"Recherche de helpers en double…",
                                    u"중복 헬퍼 확인 중…", u"Buscando helpers duplicados…"),
"No duplicate helper detected": (u"Kein doppelter Helper gefunden", u"Aucun helper en double",
                                 u"중복 헬퍼 없음", u"Sin helpers duplicados"),
"Removing leftover helper…": (u"Übrig gebliebenen Helper entfernen…", u"Suppression du helper restant…",
                              u"남은 헬퍼 제거 중…", u"Quitando el helper sobrante…"),
"A helper from before the update is still running.":
    (u"Ein Helper von vor dem Update läuft noch.",
     u"Un helper d'avant la mise à jour tourne encore.",
     u"업데이트 이전의 헬퍼가 아직 실행 중입니다.",
     u"Sigue activo un helper anterior a la actualización."),
"Checking controller mode…": (u"Controller-Modus wird geprüft…", u"Vérification du mode manette…",
                              u"컨트롤러 모드 확인 중…", u"Comprobando el modo del mando…"),
"These install kernel drivers. Without the restart everything looks installed and the virtual controller silently does not work.":
    (u"Diese installieren Kerneltreiber. Ohne Neustart sieht alles installiert aus und der virtuelle Controller funktioniert stillschweigend nicht.",
     u"Ceux-ci installent des pilotes noyau. Sans redémarrage tout semble installé et la manette virtuelle ne marche pas, sans le dire.",
     u"이들은 커널 드라이버를 설치합니다. 재부팅하지 않으면 설치된 것처럼 보이지만 가상 컨트롤러가 조용히 동작하지 않습니다.",
     u"Estos instalan controladores de núcleo. Sin reiniciar todo parece instalado y el mando virtual no funciona en silencio."),
"Expected for a same-version reinstall — the helper doesn't restart or show a UAC prompt when nothing changed.":
    (u"Bei einer Neuinstallation derselben Version normal — der Helper startet nicht neu und zeigt kein UAC, wenn sich nichts geändert hat.",
     u"Normal pour une réinstallation de la même version — le helper ne redémarre pas et n'affiche pas d'UAC si rien n'a changé.",
     u"같은 버전을 다시 설치할 때는 정상입니다 — 변경이 없으면 헬퍼는 재시작하지도, UAC를 띄우지도 않습니다.",
     u"Normal al reinstalar la misma versión — el helper no se reinicia ni muestra UAC si nada cambió."),
"Waiting for the ClawTweaks helper to start. If the Game Bar opened but nothing happens, select the ClawTweaks widget once.":
    (u"Warte auf den Start des ClawTweaks-Helpers. Wenn die Game Bar offen ist und nichts passiert, das ClawTweaks-Widget einmal anwählen.",
     u"Attente du démarrage du helper ClawTweaks. Si la Game Bar est ouverte sans rien faire, sélectionnez une fois le widget ClawTweaks.",
     u"ClawTweaks 헬퍼가 시작되기를 기다리는 중입니다. Game Bar가 열렸는데 아무 일도 없으면 ClawTweaks 위젯을 한 번 선택하세요.",
     u"Esperando a que arranque el helper de ClawTweaks. Si la Game Bar se abrió y no pasa nada, selecciona una vez el widget."),
"New update — background helper started": (u"Neues Update — Hintergrund-Helper gestartet",
                                           u"Nouvelle mise à jour — helper démarré",
                                           u"새 업데이트 — 백그라운드 헬퍼 시작됨",
                                           u"Nueva actualización — helper iniciado"),
"Installed — background helper started": (u"Installiert — Hintergrund-Helper gestartet",
                                          u"Installé — helper démarré",
                                          u"설치됨 — 백그라운드 헬퍼 시작됨",
                                          u"Instalado — helper iniciado"),
"Open Windows Settings, go to Apps, find \"ClawTweaks Center\" and uninstall it, then press Re-check.":
    (u"Windows-Einstellungen öffnen, zu Apps gehen, \"ClawTweaks Center\" suchen und deinstallieren, dann Neu prüfen drücken.",
     u"Ouvrez les Paramètres Windows, allez dans Applications, trouvez \"ClawTweaks Center\", désinstallez-le, puis Revérifier.",
     u"Windows 설정을 열고 앱에서 \"ClawTweaks Center\"를 찾아 제거한 뒤 다시 확인을 누르세요.",
     u"Abre Configuración de Windows, ve a Aplicaciones, busca \"ClawTweaks Center\", desinstálalo y pulsa Recomprobar."),
}
