I want to make a desktop app for windows which will

1. make a screenshot on ctrl+prscr keybinding
2. let me select an area. make a fade screen and write a label at the cursor (select an area)
3. after i choose an area show it without fade. 
   1. on the edge show the size in px, 
   2. show boundary box controls that let to change the boundaries like in all graphic editors
   2. show 2 actions: copy in buffer (saves a pic in buffer), save to file (opens an explorer window).
4. Esc cancels the whole operation
5. check the UI scale parameter in windows and scale the screenshot back to the 100% so i can paste it in figma and have a precise physical size of the image.

nice. it works in figma. and do not work in other apps, but its ok.
now regarding ui:
i want us to have 3 buttons:
1. Copy for Figma - labeled button which copy image in svg and make it exact 100% (as i see it on screen. ). add a tooltip with describes a specifics (ignoring windows UI scale). that we make a png and it paste in figma in frame. Add an icon 
2. Copy - add a icon. this one is storing a big bitmap which is keeping all the pixels add a tooltip which describe the action (that we save a bitmap of )
3. Save - add a icon

remove button with 2x 1x multilpliers