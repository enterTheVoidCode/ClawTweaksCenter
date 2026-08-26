# -*- coding: utf-8 -*-
"""Round thirteen: the FAQ.

"FAQ" itself is not in here. It is the tile title and the screen heading, and it is the same three
letters in all four languages anyway.
"""

FAQ = {

# ---- the screen ----------------------------------------------------------
"Answers to the questions that come up most.":
    (u"Antworten auf die häufigsten Fragen.",
     u"Réponses aux questions les plus fréquentes.",
     u"자주 나오는 질문에 대한 답변입니다.",
     u"Respuestas a las preguntas más frecuentes."),
"Press Ⓐ on a question to open it.":
    (u"Ⓐ auf einer Frage öffnet sie.",
     u"Appuie sur Ⓐ sur une question pour l'ouvrir.",
     u"질문에서 Ⓐ를 누르면 열립니다.",
     u"Pulsa Ⓐ en una pregunta para abrirla."),
"Open all":  (u"Alle öffnen", u"Tout ouvrir", u"모두 열기", u"Abrir todo"),
"Close all": (u"Alle schließen", u"Tout fermer", u"모두 닫기", u"Cerrar todo"),
"Close":     (u"Schließen", u"Fermer", u"닫기", u"Cerrar"),

# ---- 1. virtual controller -----------------------------------------------
"What is the virtual controller for?":
    (u"Wofür ist der virtuelle Controller da?",
     u"À quoi sert la manette virtuelle ?",
     u"가상 컨트롤러는 왜 필요한가요?",
     u"¿Para qué sirve el mando virtual?"),
"It replaces the Claw's own gamepad with one ClawTweaks drives.":
    (u"Er ersetzt das Gamepad der Claw durch eines, das ClawTweaks steuert.",
     u"Elle remplace la manette de la Claw par une que ClawTweaks pilote.",
     u"Claw의 기본 게임패드를 ClawTweaks가 제어하는 패드로 대체합니다.",
     u"Sustituye el mando de la Claw por uno que controla ClawTweaks."),
"Button remaps, gyro and per-game controller profiles need it.":
    (u"Tastenbelegung, Gyro und Controller-Profile pro Spiel brauchen ihn.",
     u"Le remappage, le gyro et les profils manette par jeu en dépendent.",
     u"버튼 재지정, 자이로, 게임별 컨트롤러 프로필에 필요합니다.",
     u"El remapeo, el giroscopio y los perfiles por juego lo necesitan."),
"Without it the gamepad still works, but those settings do nothing.":
    (u"Ohne ihn läuft das Gamepad weiter, aber diese Einstellungen tun nichts.",
     u"Sans elle la manette marche, mais ces réglages n'ont aucun effet.",
     u"없어도 게임패드는 작동하지만 그 설정들은 아무 효과가 없습니다.",
     u"Sin él el mando funciona, pero esos ajustes no hacen nada."),
"Turn it on in Onboarding. It switches itself back if no pad appears.":
    (u"Im Onboarding einschalten. Erscheint kein Pad, schaltet er sich zurück.",
     u"Active-la dans l'Onboarding. Si aucune manette n'apparaît, elle revient en arrière.",
     u"온보딩에서 켜세요. 패드가 나타나지 않으면 자동으로 되돌립니다.",
     u"Actívalo en Onboarding. Si no aparece un mando, vuelve solo."),

# ---- 2. MSI Center M -----------------------------------------------------
"Do I have to switch MSI Center M off?":
    (u"Muss ich MSI Center M abschalten?",
     u"Dois-je désactiver MSI Center M ?",
     u"MSI Center M을 꺼야 하나요?",
     u"¿Tengo que desactivar MSI Center M?"),
"Yes, if ClawTweaks should own the controller, the fan and the LEDs.":
    (u"Ja, wenn ClawTweaks Controller, Lüfter und LEDs führen soll.",
     u"Oui, si ClawTweaks doit gérer la manette, le ventilateur et les LED.",
     u"예, ClawTweaks가 컨트롤러와 팬, LED를 맡아야 한다면요.",
     u"Sí, si ClawTweaks debe llevar el mando, el ventilador y los LED."),
"Both write the same hardware, and the last one to write wins.":
    (u"Beide schreiben dieselbe Hardware, und der letzte Schreiber gewinnt.",
     u"Les deux écrivent le même matériel, et le dernier l'emporte.",
     u"둘 다 같은 하드웨어에 쓰며, 마지막에 쓴 쪽이 이깁니다.",
     u"Ambos escriben el mismo hardware, y gana el último."),
"Onboarding switches it off. Uninstall ClawTweaks switches it back on.":
    (u"Das Onboarding schaltet es ab, die Deinstallation wieder an.",
     u"L'Onboarding le désactive, la désinstallation le réactive.",
     u"온보딩이 끄고, 제거 화면이 다시 켭니다.",
     u"Onboarding lo apaga, y la desinstalación lo vuelve a encender."),

# ---- 3. uninstall --------------------------------------------------------
"How do I uninstall everything?":
    (u"Wie deinstalliere ich alles?",
     u"Comment tout désinstaller ?",
     u"전부 제거하려면 어떻게 하나요?",
     u"¿Cómo lo desinstalo todo?"),
"Open Uninstall ClawTweaks on the start screen and work down the list.":
    (u"Auf dem Startbildschirm Uninstall ClawTweaks öffnen und die Liste abarbeiten.",
     u"Ouvre Uninstall ClawTweaks sur l'écran d'accueil et suis la liste.",
     u"시작 화면에서 Uninstall ClawTweaks를 열고 목록을 따라가세요.",
     u"Abre Uninstall ClawTweaks en la pantalla de inicio y sigue la lista."),
"Step 1 puts the charge limit, the fan and the controller back.":
    (u"Schritt 1 setzt Ladelimit, Lüfter und Controller zurück.",
     u"L'étape 1 restaure la limite de charge, le ventilateur et la manette.",
     u"1단계가 충전 제한과 팬, 컨트롤러를 되돌립니다.",
     u"El paso 1 restaura el límite de carga, el ventilador y el mando."),
"Do that before removing the app: afterwards nothing can undo them.":
    (u"Das vor dem Entfernen der App: danach kann es nichts mehr zurücknehmen.",
     u"Fais-le avant de supprimer l'app : après, plus rien ne peut les annuler.",
     u"앱을 지우기 전에 하세요. 그 뒤에는 되돌릴 수단이 없습니다.",
     u"Hazlo antes de quitar la app: después nada puede deshacerlo."),
"The last step removes Center and always works.":
    (u"Der letzte Schritt entfernt Center und geht immer.",
     u"La dernière étape supprime Center et fonctionne toujours.",
     u"마지막 단계는 Center를 제거하며 항상 실행됩니다.",
     u"El último paso quita Center y siempre funciona."),

# ---- 4. admin rights -----------------------------------------------------
"Why does ClawTweaks ask for admin rights?":
    (u"Warum fragt ClawTweaks nach Adminrechten?",
     u"Pourquoi ClawTweaks demande-t-il les droits admin ?",
     u"ClawTweaks는 왜 관리자 권한을 요구하나요?",
     u"¿Por qué ClawTweaks pide permisos de administrador?"),
"Once, at the first install, to register its background task.":
    (u"Einmal bei der Erstinstallation, um seine Hintergrundaufgabe anzulegen.",
     u"Une fois, à la première installation, pour créer sa tâche de fond.",
     u"최초 설치 때 한 번, 백그라운드 작업을 등록하기 위해서입니다.",
     u"Una vez, en la primera instalación, para registrar su tarea de fondo."),
"Updates cost no prompt — the task does not carry a version number.":
    (u"Updates kosten keine Abfrage — die Aufgabe trägt keine Versionsnummer.",
     u"Les mises à jour ne demandent rien — la tâche ne porte pas de version.",
     u"업데이트에는 창이 뜨지 않습니다. 작업에 버전 번호가 없기 때문입니다.",
     u"Las actualizaciones no piden nada: la tarea no lleva número de versión."),
"The signing certificate uses Windows' own prompt, once per device.":
    (u"Das Zertifikat nutzt Windows' eigene Abfrage, einmal pro Gerät.",
     u"Le certificat passe par la fenêtre de Windows, une fois par appareil.",
     u"서명 인증서는 Windows 자체 창을 쓰며, 기기당 한 번입니다.",
     u"El certificado usa la ventana de Windows, una vez por dispositivo."),
"Center itself never asks for admin rights.":
    (u"Center selbst fragt nie nach Adminrechten.",
     u"Center lui-même ne demande jamais les droits admin.",
     u"Center 자체는 관리자 권한을 요구하지 않습니다.",
     u"Center nunca pide permisos de administrador."),

# ---- 5. the helper -------------------------------------------------------
"What does the background helper do?":
    (u"Was macht der Hintergrund-Helper?",
     u"Que fait le helper en arrière-plan ?",
     u"백그라운드 헬퍼는 무엇을 하나요?",
     u"¿Qué hace el helper en segundo plano?"),
"It writes TDP, fan curve, LEDs, controller and the on-screen display.":
    (u"Er schreibt TDP, Lüfterkurve, LEDs, Controller und das Overlay.",
     u"Il écrit le TDP, la courbe du ventilateur, les LED, la manette et l'overlay.",
     u"TDP, 팬 커브, LED, 컨트롤러, 화면 표시를 씁니다.",
     u"Escribe el TDP, la curva del ventilador, los LED, el mando y el overlay."),
"The widget shows what the helper does; it changes nothing by itself.":
    (u"Das Widget zeigt, was der Helper tut; es ändert selbst nichts.",
     u"Le widget montre ce que fait le helper ; il ne change rien lui-même.",
     u"위젯은 헬퍼가 하는 일을 보여줄 뿐, 스스로 바꾸지 않습니다.",
     u"El widget muestra lo que hace el helper; no cambia nada por sí solo."),
"It starts with Windows through its scheduled task.":
    (u"Er startet mit Windows über seine geplante Aufgabe.",
     u"Il démarre avec Windows via sa tâche planifiée.",
     u"예약 작업을 통해 Windows와 함께 시작합니다.",
     u"Arranca con Windows mediante su tarea programada."),
"Open the Game Bar once if it is not running.":
    (u"Öffne einmal die Game Bar, falls er nicht läuft.",
     u"Ouvre le Game Bar une fois s'il ne tourne pas.",
     u"실행되지 않으면 게임 바를 한 번 여세요.",
     u"Abre la Game Bar una vez si no está en marcha."),

# ---- 6. backups ----------------------------------------------------------
"Where are my settings, and how do I back them up?":
    (u"Wo sind meine Einstellungen, und wie sichere ich sie?",
     u"Où sont mes réglages, et comment les sauvegarder ?",
     u"설정은 어디에 있고, 어떻게 백업하나요?",
     u"¿Dónde están mis ajustes y cómo los guardo?"),
"Open Reset · Backup · Restore on the start screen.":
    (u"Auf dem Startbildschirm Reset · Backup · Restore öffnen.",
     u"Ouvre Reset · Backup · Restore sur l'écran d'accueil.",
     u"시작 화면에서 Reset · Backup · Restore를 여세요.",
     u"Abre Reset · Backup · Restore en la pantalla de inicio."),
"Create Backup writes one ZIP to Documents\\ClawTweaks\\Backups.":
    (u"Create Backup schreibt eine ZIP nach Dokumente\\ClawTweaks\\Backups.",
     u"Create Backup écrit un ZIP dans Documents\\ClawTweaks\\Backups.",
     u"Create Backup은 Documents\\ClawTweaks\\Backups에 ZIP 하나를 씁니다.",
     u"Create Backup escribe un ZIP en Documentos\\ClawTweaks\\Backups."),
"Restore Backup takes a safety copy before it writes.":
    (u"Restore Backup legt vorher eine Sicherheitskopie an.",
     u"Restore Backup fait une copie de sécurité avant d'écrire.",
     u"Restore Backup은 쓰기 전에 안전 복사본을 만듭니다.",
     u"Restore Backup hace una copia de seguridad antes de escribir."),
"A full reset backs up your settings first as well.":
    (u"Auch ein Full Reset sichert vorher deine Einstellungen.",
     u"Une réinitialisation complète sauvegarde aussi tes réglages d'abord.",
     u"전체 초기화도 먼저 설정을 백업합니다.",
     u"Un restablecimiento completo también guarda antes tus ajustes."),

# ---- 7. the library ------------------------------------------------------
"Do I need the Game Bar for the game library?":
    (u"Brauche ich die Game Bar für die Spielebibliothek?",
     u"Ai-je besoin du Game Bar pour la bibliothèque ?",
     u"게임 라이브러리에 게임 바가 필요한가요?",
     u"¿Necesito la Game Bar para la biblioteca?"),
"No. The library runs on its own, without the Game Bar.":
    (u"Nein. Die Bibliothek läuft eigenständig, ohne die Game Bar.",
     u"Non. La bibliothèque fonctionne seule, sans le Game Bar.",
     u"아니요. 라이브러리는 게임 바 없이 단독으로 동작합니다.",
     u"No. La biblioteca funciona sola, sin la Game Bar."),
"Only the ClawTweaks widget lives in the Game Bar.":
    (u"Nur das ClawTweaks-Widget sitzt in der Game Bar.",
     u"Seul le widget ClawTweaks vit dans le Game Bar.",
     u"게임 바에 있는 것은 ClawTweaks 위젯뿐입니다.",
     u"Solo el widget de ClawTweaks vive en la Game Bar."),
"Set the library as the screen Center opens on in Library Settings.":
    (u"In den Library Settings die Bibliothek als Startbildschirm setzen.",
     u"Dans Library Settings, choisis la bibliothèque comme écran de départ.",
     u"Library Settings에서 라이브러리를 시작 화면으로 지정하세요.",
     u"En Library Settings elige la biblioteca como pantalla de inicio."),

# ---- 8. the MSI button ---------------------------------------------------
"The MSI button does not open the widget.":
    (u"Die MSI-Taste öffnet das Widget nicht.",
     u"Le bouton MSI n'ouvre pas le widget.",
     u"MSI 버튼을 눌러도 위젯이 열리지 않습니다.",
     u"El botón MSI no abre el widget."),
"Open Onboarding and enter the slot ClawTweaks sits at in the Game Bar.":
    (u"Im Onboarding die Position eintragen, an der ClawTweaks in der Game Bar sitzt.",
     u"Dans l'Onboarding, indique la position de ClawTweaks dans le Game Bar.",
     u"온보딩에서 게임 바의 ClawTweaks 위치 번호를 입력하세요.",
     u"En Onboarding indica la posición de ClawTweaks en la Game Bar."),
"The helper hops to that slot; it cannot read the position itself.":
    (u"Der Helper springt dorthin; auslesen kann er die Position nicht.",
     u"Le helper saute jusque-là ; il ne peut pas lire la position.",
     u"헬퍼는 그 위치로 이동합니다. 위치를 직접 읽을 수는 없습니다.",
     u"El helper salta a esa posición; no puede leerla por su cuenta."),
"Raise \"Wait before jumping\" in the widget if a game is busy.":
    (u"Im Widget „Wait before jumping“ erhöhen, wenn ein Spiel ausgelastet ist.",
     u"Augmente « Wait before jumping » dans le widget si un jeu charge le système.",
     u"게임이 바쁠 때는 위젯에서 “Wait before jumping” 값을 올리세요.",
     u"Sube «Wait before jumping» en el widget si un juego va cargado."),
}
